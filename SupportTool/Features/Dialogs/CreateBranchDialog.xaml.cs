using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace SupportTool.Features.Dialogs
{
    public sealed partial class CreateBranchDialog : ContentDialog
    {
        public string BranchName { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool ShouldCreate { get; private set; } = false;
        public string SelectedBaseBranch { get; set; } = string.Empty;
        public List<string> AvailableBranches { get; set; } = new List<string>();

        public CreateBranchDialog(string branchName, List<string> availableBranches)
        {
            InitializeComponent();
            BranchName = branchName;
            AvailableBranches = availableBranches ?? new List<string>();
            
            // Find default branch (prefer main, then master, then first branch)
            SelectedBaseBranch = FindDefaultBranch(AvailableBranches);
        }

        private string FindDefaultBranch(List<string> branches)
        {
            if (branches == null || branches.Count == 0)
                return string.Empty;

            // Check if main exists
            if (branches.Any(b => b.Equals("main", StringComparison.OrdinalIgnoreCase)))
            {
                return "main";
            }
            
            // Check if master exists
            if (branches.Any(b => b.Equals("master", StringComparison.OrdinalIgnoreCase)))
            {
                return "master";
            }

            // If neither exists, return the first branch
            return branches.FirstOrDefault() ?? string.Empty;
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (string.IsNullOrEmpty(SelectedBaseBranch))
            {
                args.Cancel = true;
                ErrorInfoBar.Message = "Please select a base branch";
                ErrorInfoBar.IsOpen = true;
                return;
            }

            ShouldCreate = true;
        }
    }
}
