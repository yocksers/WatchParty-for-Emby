using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Plugins;

namespace WatchPartyForEmby
{
    public static class PasswordHelper
    {
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(password);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
        
        public static bool VerifyPassword(string password, string hash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;
            var passwordHash = HashPassword(password);
            return passwordHash == hash;
        }
    }

    public class PartyParticipant
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string SessionId { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPaused { get; set; }
        public bool IsBuffering { get; set; }
        public bool IsReady { get; set; }
        
        public PartyParticipant()
        {
            JoinedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;
            IsReady = false;
        }
    }

    public class WatchPartyItem
    {
        public string Id { get; set; }
        public string LibraryId { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public string SeriesId { get; set; }
        public string SeasonId { get; set; }
        public string CollectionName { get; set; }
        public string TargetLibraryId { get; set; }
        public string TargetLibraryPath { get; set; }
        public bool IsActive { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public int MaxParticipants { get; set; }
        public DateTime CreatedDate { get; set; }
        
        public List<string> AllowedUserIds { get; set; }
        public string HostUserId { get; set; }
        public string MasterUserId { get; set; }
        public string Password { get; set; }
        public string PasswordHash { get; set; }
        public bool IsWaitingRoom { get; set; }
        public DateTime? ScheduledStartTime { get; set; }
        public bool AutoStartWhenReady { get; set; }
        public int MinReadyCount { get; set; }
        public string PauseControl { get; set; }
        public bool HostOnlySeek { get; set; }
        public bool LockSeekAhead { get; set; }
        public int SyncToleranceSeconds { get; set; }
        public int MaxBufferThresholdSeconds { get; set; }
        public bool AutoKickInactiveMinutes { get; set; }
        public int InactiveTimeoutMinutes { get; set; }
        public bool EnableNetworkLatencyCompensation { get; set; }
        public int NetworkLatencyMeasurementIntervalSeconds { get; set; }
        public bool AutoAdjustForLatency { get; set; }
        public int MaxLatencyCompensationMs { get; set; }

        public WatchPartyItem()
        {
            Id = Guid.NewGuid().ToString();
            CollectionName = "Watch Party";
            IsActive = true;
            MaxParticipants = 50;
            CreatedDate = DateTime.UtcNow;
            AllowedUserIds = new List<string>();
            IsWaitingRoom = true;
            AutoStartWhenReady = true;
            MinReadyCount = 1;
            PauseControl = "Anyone";
            HostOnlySeek = true;
            LockSeekAhead = true;
            SyncToleranceSeconds = 10;
            MaxBufferThresholdSeconds = 30;
            AutoKickInactiveMinutes = true;
            InactiveTimeoutMinutes = 15;
            EnableNetworkLatencyCompensation = true;
            NetworkLatencyMeasurementIntervalSeconds = 30;
            AutoAdjustForLatency = true;
            MaxLatencyCompensationMs = 5000;
        }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string SelectedLibraryId { get; set; }
        public string SelectedItemId { get; set; }
        public string SelectedItemName { get; set; }
        public string SelectedItemType { get; set; }
        public string SelectedSeriesId { get; set; }
        public string SelectedSeasonId { get; set; }
        public string CollectionName { get; set; } = "Watch Party";
        public bool IsPartyActive { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public int MaxParticipants { get; set; } = 50;

        public List<WatchPartyItem> WatchParties { get; set; }

        public int SyncIntervalSeconds { get; set; } = 5;
        public int SyncOffsetMilliseconds { get; set; } = 1000;
        public bool EnableDebugLogging { get; set; }
        public string WatchPartyStrmPath { get; set; }

        public bool EnableExternalWebServer { get; set; } = true;
        public int ExternalWebServerPort { get; set; } = 8097;
        public string AdminPasswordHash { get; set; }
        public string EmbyApiKey { get; set; }
        public string StrmTargetLibraryId { get; set; }
        public string StrmTargetLibraryName { get; set; }
        public string ExternalServerUrl { get; set; }

        public PluginConfiguration()
        {
            SelectedLibraryId = null;
            SelectedItemId = null;
            SelectedItemName = "No content selected";
            SelectedItemType = null;
            SelectedSeriesId = null;
            SelectedSeasonId = null;
            CollectionName = "Watch Party";
            IsPartyActive = false;
            EnableDebugLogging = false;
            CurrentPositionTicks = 0;
            IsPlaying = false;
            WatchParties = new List<WatchPartyItem>();
            SyncIntervalSeconds = 5;
            SyncOffsetMilliseconds = 1000;
            WatchPartyStrmPath = string.Empty;
            EnableExternalWebServer = true;
            ExternalWebServerPort = 8097;
            AdminPasswordHash = string.Empty;
            ExternalServerUrl = string.Empty;
        }
    }
}
