using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SupportTool.Features.Alerts.Helpers;
using Windows.Storage;
using System.Threading.Tasks;
using SupportTool.Features.Alerts.Models;

namespace SupportTool.Features.Alerts.Services
{
    public class AlertService
    {
        private static readonly string StacksPath = ConfigLoader.Get<string>("Alert_Directory_Path", "metaform\\mpm\\copies\\production\\prd\\eu-west-1");
        private readonly string[] _requiredFolders = [".github", "ansible", "metaform", "terraform"];
        private readonly SettingsService _settings = new();
        private readonly NewRelicApiService _newRelicApiService = new();

        public AlertService()
        {
        }

        public string RepositoryPath
        {
            get => _settings.GetSetting("NRAlertsDir");
            private set => _settings.SetSetting("NRAlertsDir", value);
        }

        public string SelectedStack
        {
            get => _settings.GetSetting("SelectedStack");
            set => _settings.SetSetting("SelectedStack", value);
        }

        public List<NrqlAlert> GetAlertsForStack(string stackName)
        {
            var tfvarsPath = Path.Combine(RepositoryPath, StacksPath, stackName, "auto.tfvars");
            if (!File.Exists(tfvarsPath))
                return [];

            var parser = new HclParser();
            return parser.ParseAlerts(File.ReadAllText(tfvarsPath));
        }

        public void SaveAlertsToFile(string stackName, List<NrqlAlert> alerts)
        {
            try
            {
                var filePath = Path.Combine(RepositoryPath, StacksPath, stackName, "auto.tfvars");
                var parser = new HclParser();
                var updatedContent = parser.ReplaceNrqlAlertsSection(
                    File.ReadAllText(filePath),
                    alerts);
                File.WriteAllText(filePath, updatedContent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public bool ValidateRepository(string folderPath, out string[] missingFolders)
        {
            try
            {
                var existingFolders = Directory.GetDirectories(folderPath)
                    .Select(path => new DirectoryInfo(path).Name)
                    .ToArray();

                missingFolders = _requiredFolders.Except(existingFolders).ToArray();
                return !missingFolders.Any();
            }
            catch (Exception)
            {
                missingFolders = _requiredFolders;
                return false;
            }
        }

        public List<string> ValidateAlertInputs(NrqlAlert alert, List<NrqlAlert> existingAlerts, bool checkForDuplicates = false)
        {
            var errors = new List<string>();

            if (alert == null)
            {
                errors.Add("Alert cannot be null.");
                return errors;
            }

            // Required fields with character validation
            var fieldsToValidate = new Dictionary<string, string>
                {
                    { "Name", alert.Name },
                    { "NrqlQuery", alert.NrqlQuery },
                    { "Severity", alert.Severity },
                    { "AggregationMethod", alert.AggregationMethod },
                    { "CriticalOperator", alert.CriticalOperator },
                    { "CriticalThresholdOccurrences", alert.CriticalThresholdOccurrences }
                };

            foreach (var field in fieldsToValidate)
            {
                if (string.IsNullOrWhiteSpace(field.Value))
                    errors.Add($"{field.Key} cannot be empty.");
                else if (ContainsInvalidCharacters(field.Value))
                    errors.Add($"{field.Key} contains invalid characters.");
            }

            // Name and NRQL Query length validation
            if (!string.IsNullOrWhiteSpace(alert.Name) && alert.Name.Length < 10)
                errors.Add("Name must be at least 10 characters long.");

            if (!string.IsNullOrWhiteSpace(alert.NrqlQuery) && alert.NrqlQuery.Length < 10)
                errors.Add("NRQL Query must be at least 10 characters long.");

            // Numeric field validations
            if (alert.CriticalThreshold < 0)
                errors.Add("Critical Threshold must be a non-negative number.");
            if (alert.CriticalThresholdDuration < 0)
                errors.Add("Critical Threshold Duration must be a non-negative integer.");
            if (alert.AggregationDelay < 0)
                errors.Add("Aggregation Delay must be a non-negative integer.");

            if (checkForDuplicates)
            {
                // Check for duplicate name
                if (existingAlerts.Any(a => a.Name.Equals(alert.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("An alert with this name already exists.");
                }

                // Check for duplicate NRQL query
                if (existingAlerts.Any(a => a.NrqlQuery.Equals(alert.NrqlQuery, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("An alert with this NRQL query already exists.");
                }
            }

            return errors;
        }

        private bool ContainsInvalidCharacters(string input)
        {
            if (input == null || input == string.Empty)
                return false;
            // List of invalid characters
            char[] invalidChars = {'[', ']', '{', '}' };

            // Check if the input contains any invalid characters
            return input.IndexOfAny(invalidChars) >= 0;
        }

        public string[] GetAlertStacksFromDirectories()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return [];

            var path = Path.Combine(RepositoryPath, StacksPath);
            return !Directory.Exists(path)
                ? []
                : Directory.GetDirectories(path)
                    .Select(dir => new DirectoryInfo(dir).Name)
                    .ToArray();
        }

        public bool HasCarrierAlert(List<NrqlAlert> alerts, string carrier, AlertType alertType)
        {
            return alertType switch
            {
                AlertType.PrintDuration => alerts.Any(alert =>
                    HasExactCarrierNameMatch(alert, carrier) &&
                    alert.NrqlQuery.Contains("average(duration)", StringComparison.OrdinalIgnoreCase)),
                AlertType.ErrorRate => alerts.Any(alert =>
                    HasExactCarrierNameMatch(alert, carrier) &&
                    alert.NrqlQuery.Contains("percentage", StringComparison.OrdinalIgnoreCase) &&
                    alert.NrqlQuery.Contains("Error", StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
        }

        /// <summary>
        /// Checks if an alert has an exact match for the carrier name (not a partial match)
        /// Checks the NRQL query for the pattern: CarrierName = 'carrier'
        /// </summary>
        /// <param name="alert">The alert to check</param>
        /// <param name="carrierName">The carrier name to match</param>
        /// <returns>True if exact match found, false otherwise</returns>
        public static bool HasExactCarrierNameMatch(NrqlAlert alert, string carrierName)
        {
            if (string.IsNullOrEmpty(carrierName) || string.IsNullOrEmpty(alert?.NrqlQuery))
                return false;

            // Escape single quotes in carrier name for matching
            string escapedCarrierName = carrierName.Replace("'", "\\'");
            
            // Look for exact pattern: CarrierName = 'carrierName' followed by space, comma, or end
            // This ensures "DPD" doesn't match "DPD France"
            string pattern = $"CarrierName = '{escapedCarrierName}'";
            int index = alert.NrqlQuery.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            
            if (index >= 0)
            {
                int afterPattern = index + pattern.Length;
                // Check if the character after the pattern is a space, comma, or end of string
                if (afterPattern >= alert.NrqlQuery.Length || 
                    alert.NrqlQuery[afterPattern] == ' ' || 
                    alert.NrqlQuery[afterPattern] == ',' ||
                    alert.NrqlQuery[afterPattern] == '\'' ||
                    alert.NrqlQuery[afterPattern] == ')')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a carrier ID has a DM alert of the specified type
        /// </summary>
        /// <param name="alerts">List of alerts to check</param>
        /// <param name="carrierId">The carrier ID to check for</param>
        /// <param name="alertType">The type of alert (AverageDuration or ErrorRate)</param>
        /// <param name="isAsos">Whether to check for ASOS alerts (true) or non-ASOS alerts (false). Null means check both.</param>
        /// <returns>True if the alert exists, false otherwise</returns>
        public bool HasCarrierIdAlert(List<NrqlAlert> alerts, string carrierId, AlertType alertType, bool? isAsos = null)
        {
            return alertType switch
            {
                AlertType.PrintDuration => alerts.Any(alert =>
                    alert.Name.Contains("DM Allocation", StringComparison.OrdinalIgnoreCase) &&                      // Must be DM Allocation alert
                    alert.Name.Contains("Average Duration", StringComparison.OrdinalIgnoreCase) &&                   // Must be Average Duration
                    HasExactCarrierIdMatch(alert, carrierId) &&                                                      // Find exact carrierId match
                    alert.NrqlQuery.Contains("average(duration)", StringComparison.OrdinalIgnoreCase) &&            // Find average aggregate function
                    (isAsos == null || (isAsos.Value ? alert.Name.Contains("ASOS", StringComparison.OrdinalIgnoreCase) : !alert.Name.Contains("ASOS", StringComparison.OrdinalIgnoreCase)))), // Match ASOS/non-ASOS based on parameter
                AlertType.ErrorRate => alerts.Any(alert =>
                    alert.Name.Contains("DM Allocation", StringComparison.OrdinalIgnoreCase) &&                      // Must be DM Allocation alert
                    alert.Name.Contains("Error Percentage", StringComparison.OrdinalIgnoreCase) &&                  // Must be Error Percentage
                    HasExactCarrierIdMatch(alert, carrierId) &&                                                      // Find exact carrierId match
                    alert.NrqlQuery.Contains("percentage", StringComparison.OrdinalIgnoreCase) &&                    // Find percentage aggregate function
                    alert.NrqlQuery.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    (isAsos == null || (isAsos.Value ? alert.Name.Contains("ASOS", StringComparison.OrdinalIgnoreCase) : !alert.Name.Contains("ASOS", StringComparison.OrdinalIgnoreCase)))), // Match ASOS/non-ASOS based on parameter
                _ => false
            };
        }

        /// <summary>
        /// Checks if an alert has an exact match for the carrier ID (not a partial match)
        /// Checks both the alert name (in parentheses) and the query
        /// </summary>
        /// <param name="alert">The alert to check</param>
        /// <param name="carrierId">The carrier ID to match</param>
        /// <returns>True if exact match found, false otherwise</returns>
        public static bool HasExactCarrierIdMatch(NrqlAlert alert, string carrierId)
        {
            // Check alert name for pattern: (carrierId) - this ensures exact match
            string namePattern = $"({carrierId})";
            if (alert.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Also check query for exact match: carrierId = carrierId followed by space, comma, or end
            // This prevents matching 74 when looking for 741
            string queryPattern = $"carrierId = {carrierId}";
            int index = alert.NrqlQuery.IndexOf(queryPattern, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                int afterPattern = index + queryPattern.Length;
                // Check if the character after the pattern is a space, comma, or end of string
                if (afterPattern >= alert.NrqlQuery.Length || 
                    alert.NrqlQuery[afterPattern] == ' ' || 
                    alert.NrqlQuery[afterPattern] == ',' ||
                    alert.NrqlQuery[afterPattern] == ')')
                {
                    return true;
                }
            }

            return false;
        }

        public NrqlAlert CloneAlert(NrqlAlert alert) => new()
        {
            Name = $"{alert.Name} Copy",
            Description = alert.Description,
            NrqlQuery = alert.NrqlQuery,
            RunbookUrl = alert.RunbookUrl,
            Severity = alert.Severity,
            Enabled = alert.Enabled,
            AggregationMethod = alert.AggregationMethod,
            AggregationWindow = alert.AggregationWindow,
            AggregationDelay = alert.AggregationDelay,
            CriticalOperator = alert.CriticalOperator,
            CriticalThreshold = alert.CriticalThreshold,
            CriticalThresholdDuration = alert.CriticalThresholdDuration,
            CriticalThresholdOccurrences = alert.CriticalThresholdOccurrences,
            ExpirationDuration = alert.ExpirationDuration,
            CloseViolationsOnExpiration = alert.CloseViolationsOnExpiration,
            AdditionalFields = new Dictionary<string, object>(alert.AdditionalFields)
        };

        public static double CalculateSuggestedThreshold(CarrierDurationStatistics stats)
        {
            // Get calculation method and parameters from config
            string? method = AlertTemplates.GetConfigValue<string>("PrintDuration.ProposedValues.Method");

            if (method == "StdDev")
            {
                float? k = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.StdDevMultiplier");
                float? minThreshold = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.MinimumAbsoluteThreshold");
                float? maxThreshold = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.MaximumAbsoluteThreshold");
                float? minStdDev = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.MinimumStdDev");

                if (!k.HasValue)
                {
                    throw new InvalidOperationException("'StdDevMultiplier' missing in config.");
                }

                float actualStdDev = stats.StandardDeviation;
                if (minStdDev.HasValue && actualStdDev < minStdDev.Value)
                {
                    actualStdDev = minStdDev.Value; // Use minimum configured stddev if actual is too low
                }

                double proposedDuration = stats.AverageDuration + k.Value * actualStdDev;

                // Apply min/max caps for the proposed duration
                if (minThreshold.HasValue && proposedDuration < minThreshold.Value)
                {
                    proposedDuration = minThreshold.Value;
                }
                if (maxThreshold.HasValue && proposedDuration > maxThreshold.Value)
                {
                    proposedDuration = maxThreshold.Value;
                }

                if (proposedDuration < 3)
                {
                    proposedDuration = 3;
                }

                // Round to nearest 0.5
                proposedDuration = Math.Round(proposedDuration * 2.0) / 2.0;
                
                return proposedDuration;
            }
            else
            {
                // Fallback method
                float durationMultiplier = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.FormulaMultiplier") ?? 1.5f;
                float durationOffset = AlertTemplates.GetConfigValue<float?>("PrintDuration.ProposedValues.FormulaOffset") ?? 3.0f;
                double proposedDurationFallback = Math.Round(stats.AverageDuration * durationMultiplier + durationOffset, 2);
                // Round to nearest 0.5
                proposedDurationFallback = Math.Round(proposedDurationFallback * 2.0) / 2.0;
                
                return proposedDurationFallback;
            }
        }

        public static bool IsAlertPrintDuration(NrqlAlert workingCopy)
        {
            bool hasAverageDuration = workingCopy.NrqlQuery?.ToLower().Contains("average(duration)") == true;
            
            if (!hasAverageDuration)
                return false;

            // Check for MPM alerts (PrintParcel with carrier name)
            bool hasPrintParcel = workingCopy.Name?.ToLower().Contains("printparcel") == true;
            bool hasCarrierInTitle = !string.IsNullOrEmpty(ExtractCarrierFromTitle(workingCopy.Name));
            if (hasPrintParcel && hasCarrierInTitle)
                return true;

            // Check for DM alerts (DM Allocation with carrier ID)
            bool isDmAllocation = workingCopy.Name?.Contains("DM Allocation", StringComparison.OrdinalIgnoreCase) == true;
            bool hasCarrierId = !string.IsNullOrEmpty(ExtractCarrierIdFromAlert(workingCopy.Name));
            if (isDmAllocation && hasCarrierId)
                return true;

            return false;
        }

        public static string ExtractCarrierFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return string.Empty;

            // Extract carrier name from format "Carrier - Description"  
            int dashIndex = title.IndexOf(" - ");
            if (dashIndex > 0)
            {
                return title.Substring(0, dashIndex).Trim();
            }

            return string.Empty;
        }

        /// <summary>
        /// Extracts carrier name from DM alert name format: "DM Allocation <CarrierName> (ID) ..." or "DM Allocation CarrierName (ID) ..."
        /// </summary>
        /// <param name="alertName">The alert name</param>
        /// <param name="carrierId">The carrier ID to match</param>
        /// <returns>The carrier name if found, empty string otherwise</returns>
        public static string ExtractCarrierNameFromDmAlert(string alertName, string carrierId)
        {
            if (string.IsNullOrEmpty(alertName) || string.IsNullOrEmpty(carrierId))
                return string.Empty;

            // Look for pattern: DM Allocation ... <CarrierName> (carrierId) ...
            // Example: "DM Allocation <DPD Poland API> (764) Error Percentage"
            // Or legacy: "DM Allocation FedEx API (701) Error Percentage"
            
            // First try new format with <>
            int openBracketIndex = alertName.IndexOf('<');
            if (openBracketIndex >= 0)
            {
                int closeBracketIndex = alertName.IndexOf('>', openBracketIndex);
                if (closeBracketIndex > openBracketIndex)
                {
                    // Verify the carrier ID matches by checking for (carrierId) after the closing bracket
                    int parenIndex = alertName.IndexOf($"({carrierId})", closeBracketIndex);
                    if (parenIndex > closeBracketIndex)
                    {
                        return alertName.Substring(openBracketIndex + 1, closeBracketIndex - openBracketIndex - 1).Trim();
                    }
                }
            }

            // Fallback: Try to extract from legacy format without <>
            // Pattern: "DM Allocation ***Critical*** CarrierName (ID) ..."
            int criticalIndex = alertName.IndexOf("***Critical***", StringComparison.OrdinalIgnoreCase);
            if (criticalIndex >= 0)
            {
                int startIndex = criticalIndex + "***Critical***".Length;
                int parenIndex = alertName.IndexOf($"({carrierId})", startIndex);
                if (parenIndex > startIndex)
                {
                    // Extract text between "***Critical***" and "(carrierId)"
                    string extracted = alertName.Substring(startIndex, parenIndex - startIndex).Trim();
                    // Remove any trailing spaces or special characters
                    return extracted.Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Extracts carrier ID from DM alert name format: "DM Allocation ... (ID) ..."
        /// </summary>
        /// <param name="alertName">The alert name</param>
        /// <returns>The carrier ID if found, empty string otherwise</returns>
        public static string ExtractCarrierIdFromAlert(string alertName)
        {
            if (string.IsNullOrEmpty(alertName))
                return string.Empty;
            
            // Find the first occurrence of ( followed by digits and then )
            int openParenIndex = alertName.IndexOf('(');
            if (openParenIndex >= 0)
            {
                int closeParenIndex = alertName.IndexOf(')', openParenIndex);
                if (closeParenIndex > openParenIndex)
                {
                    string potentialId = alertName.Substring(openParenIndex + 1, closeParenIndex - openParenIndex - 1).Trim();
                    // Check if it's all digits (carrier ID)
                    if (!string.IsNullOrEmpty(potentialId) && potentialId.All(char.IsDigit))
                    {
                        return potentialId;
                    }
                }
            }

            return string.Empty;
        }
    }
}