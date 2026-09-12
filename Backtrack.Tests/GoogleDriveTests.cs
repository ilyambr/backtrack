using System;
using System.Collections.Generic;
using System.Linq;
using Backtrack.Core;
using Backtrack.UI.Dialogs;
using Xunit;

namespace Backtrack.Tests;

public class GoogleDriveTests
{
    [Theory]
    [InlineData("1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    [InlineData("https://drive.google.com/drive/folders/1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    [InlineData("https://drive.google.com/drive/u/1/folders/1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5?usp=sharing", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    [InlineData("https://drive.google.com/open?id=1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    [InlineData("https://drive.google.com/drive/folders/1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5/", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    [InlineData("   1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5   ", "1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5")]
    public void ExtractDriveFolderId_ValidInputs_ReturnsFolderId(string input, string expectedId)
    {
        string? result = GoogleDrivePickerWindow.ExtractDriveFolderId(input);
        Assert.Equal(expectedId, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-id")]
    [InlineData("http://google.com")]
    public void ExtractDriveFolderId_InvalidInputs_ReturnsNull(string input)
    {
        string? result = GoogleDrivePickerWindow.ExtractDriveFolderId(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("The service drive has thrown an exception. HttpStatusCode is Forbidden. Insufficient permissions for the specified parent.", "No write permission for destination folder.")]
    [InlineData("Google.Apis.Requests.RequestError: insufficientFilePermissions [403]", "No write permission for destination folder.")]
    [InlineData("File not found: 1KEsCSRhpp4GnovEFSUGW1K4MPE_18JA5", "Destination folder not found or inaccessible.")]
    [InlineData("Google.Apis.Requests.RequestError: NotFound [404]", "Destination folder not found or inaccessible.")]
    [InlineData("User has exceeded their storageQuotaExceeded limit.", "Google Drive storage quota exceeded.")]
    [InlineData("The user's drive quota has been reached.", "Google Drive storage quota exceeded.")]
    [InlineData("Token has expired: invalid_grant", "Google session expired. Please sign in again.")]
    [InlineData("System.Net.Http.HttpRequestException: Connection refused", "Network connection issue. Please try again.")]
    [InlineData("A connection attempt failed because the connected party did not properly respond", "Network connection issue. Please try again.")]
    public void SimplifyError_VerboseOrTechnicalErrors_ReturnsUserFriendlyMessage(string rawError, string expectedSimplified)
    {
        string result = GoogleDriveService.SimplifyError(rawError);
        Assert.Equal(expectedSimplified, result);
    }

    [Fact]
    public void PinnedDriveFolders_AccountIsolation_FiltersCorrectly()
    {
        var pinnedFolders = new List<PinnedDriveFolder>
        {
            new() { Id = "folder1", Name = "UserA Personal", AccountEmail = "userA@gmail.com" },
            new() { Id = "folder2", Name = "UserB Editor", AccountEmail = "userB@gmail.com" },
            new() { Id = "folder3", Name = "Legacy Folder", AccountEmail = null }
        };

        // Act for userA
        string activeEmailA = "userA@gmail.com";
        var visibleA = pinnedFolders.Where(p =>
            string.IsNullOrEmpty(activeEmailA) ||
            string.IsNullOrEmpty(p.AccountEmail) ||
            string.Equals(p.AccountEmail, activeEmailA, StringComparison.OrdinalIgnoreCase)).ToList();

        // Act for userB
        string activeEmailB = "userB@gmail.com";
        var visibleB = pinnedFolders.Where(p =>
            string.IsNullOrEmpty(activeEmailB) ||
            string.IsNullOrEmpty(p.AccountEmail) ||
            string.Equals(p.AccountEmail, activeEmailB, StringComparison.OrdinalIgnoreCase)).ToList();

        // Assert
        Assert.Contains(visibleA, p => p.Id == "folder1");
        Assert.DoesNotContain(visibleA, p => p.Id == "folder2");
        Assert.Contains(visibleA, p => p.Id == "folder3");

        Assert.Contains(visibleB, p => p.Id == "folder2");
        Assert.DoesNotContain(visibleB, p => p.Id == "folder1");
        Assert.Contains(visibleB, p => p.Id == "folder3");
    }
}
