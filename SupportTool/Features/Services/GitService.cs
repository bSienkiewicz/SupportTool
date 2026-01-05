using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

namespace SupportTool.Features.Services
{
    public class GitService
    {
        /// <summary>
        /// Gets the current git branch name from the repository
        /// </summary>
        public static string? GetCurrentBranch(string? repositoryPath = null)
        {
            try
            {
                string? gitRoot = FindGitRoot(repositoryPath);
                if (gitRoot == null)
                    return null;

                string headPath = Path.Combine(gitRoot, ".git", "HEAD");
                if (!File.Exists(headPath))
                    return null;

                string headContent = File.ReadAllText(headPath).Trim();
                
                // Check if we're in detached HEAD state (starts with commit hash)
                if (headContent.Length == 40 && headContent.All(c => char.IsLetterOrDigit(c)))
                {
                    return "detached HEAD";
                }

                // Check if it's a ref (format: "ref: refs/heads/branch-name")
                if (headContent.StartsWith("ref: refs/heads/"))
                {
                    return headContent.Substring(16); // Remove "ref: refs/heads/"
                }

                // Try to get branch from packed-refs or other methods
                return GetBranchFromPackedRefs(gitRoot, headContent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting git branch: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the git repository root by walking up the directory tree
        /// </summary>
        public static string? FindGitRoot(string? startPath)
        {
            try
            {
                string? currentDir = startPath ?? Directory.GetCurrentDirectory();
                
                // If startPath is a file, get its directory
                if (File.Exists(currentDir))
                {
                    currentDir = Path.GetDirectoryName(currentDir);
                }

                if (string.IsNullOrEmpty(currentDir))
                    return null;

                DirectoryInfo? dir = new DirectoryInfo(currentDir);
                
                while (dir != null)
                {
                    string gitDir = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    {
                        return dir.FullName;
                    }
                    
                    dir = dir.Parent;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Attempts to get branch name from packed-refs file (for detached HEAD cases)
        /// </summary>
        private static string? GetBranchFromPackedRefs(string gitRoot, string commitHash)
        {
            try
            {
                string packedRefsPath = Path.Combine(gitRoot, ".git", "packed-refs");
                if (!File.Exists(packedRefsPath))
                    return null;

                string[] lines = File.ReadAllLines(packedRefsPath);
                foreach (string line in lines)
                {
                    if (line.Trim().StartsWith(commitHash))
                    {
                        // Format: "commit-hash refs/heads/branch-name"
                        string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[1].StartsWith("refs/heads/"))
                        {
                            return parts[1].Substring(11); // Remove "refs/heads/"
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        /// <summary>
        /// Gets the current git branch name asynchronously
        /// </summary>
        public static async Task<string?> GetCurrentBranchAsync(string? repositoryPath = null)
        {
            return await Task.Run(() => GetCurrentBranch(repositoryPath));
        }

        /// <summary>
        /// Gets all git branches from the repository
        /// </summary>
        public static async Task<List<string>> GetAllBranchesAsync(string? repositoryPath = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string? gitRoot = FindGitRoot(repositoryPath);
                    if (gitRoot == null)
                        return new List<string>();

                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "branch",
                        WorkingDirectory = gitRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = System.Diagnostics.Process.Start(processStartInfo);
                    if (process == null)
                        return new List<string>();

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    var branches = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => b.Trim().TrimStart('*', ' '))
                        .Where(b => !string.IsNullOrEmpty(b))
                        .ToList();

                    return branches;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting git branches: {ex.Message}");
                    return new List<string>();
                }
            });
        }

        /// <summary>
        /// Checks out a git branch
        /// </summary>
        public static async Task<bool> CheckoutBranchAsync(string branchName, string? repositoryPath = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string? gitRoot = FindGitRoot(repositoryPath);
                    if (gitRoot == null)
                        return false;

                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"checkout {branchName}",
                        WorkingDirectory = gitRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = System.Diagnostics.Process.Start(processStartInfo);
                    if (process == null)
                        return false;

                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking out branch: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Creates and checks out a new branch from the specified base branch
        /// </summary>
        public static async Task<bool> CreateAndCheckoutBranchAsync(string branchName, string baseBranch, string? repositoryPath = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string? gitRoot = FindGitRoot(repositoryPath);
                    if (gitRoot == null)
                        return false;

                    if (string.IsNullOrEmpty(baseBranch))
                    {
                        // Detect default branch from repository
                        baseBranch = DetectDefaultBranch(gitRoot);
                        if (string.IsNullOrEmpty(baseBranch))
                        {
                            return false; // Could not determine default branch
                        }
                    }

                    // Create and checkout new branch
                    var checkoutProcessInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"checkout -b {branchName} {baseBranch}",
                        WorkingDirectory = gitRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = System.Diagnostics.Process.Start(checkoutProcessInfo);
                    if (process == null)
                        return false;

                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error creating branch: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Detects the default branch (main or master) from the repository
        /// </summary>
        private static string DetectDefaultBranch(string gitRoot)
        {
            try
            {
                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "branch",
                    WorkingDirectory = gitRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process == null)
                    return string.Empty;

                string branchOutput = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Check for main first (preferred), then master
                if (branchOutput.Contains("main") || branchOutput.Contains("* main"))
                {
                    return "main";
                }
                
                if (branchOutput.Contains("master") || branchOutput.Contains("* master"))
                {
                    return "master";
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error detecting default branch: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
