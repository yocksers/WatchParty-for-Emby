using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Plugins;

namespace WatchPartyForEmby
{
    public static class PasswordHelper
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 10000;

        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations))
            {
                var hash = pbkdf2.GetBytes(HashSize);
                var hashBytes = new byte[SaltSize + HashSize];
                Array.Copy(salt, 0, hashBytes, 0, SaltSize);
                Array.Copy(hash, 0, hashBytes, SaltSize, HashSize);
                return Convert.ToBase64String(hashBytes);
            }
        }
        
        public static bool VerifyPassword(string password, string hash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;
            
            try
            {
                var hashBytes = Convert.FromBase64String(hash);
                
                // Check if it's a new PBKDF2 hash (salt + hash = 48 bytes)
                if (hashBytes.Length == SaltSize + HashSize)
                {
                    var salt = new byte[SaltSize];
                    Array.Copy(hashBytes, 0, salt, 0, SaltSize);
                    
                    using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations))
                    {
                        var testHash = pbkdf2.GetBytes(HashSize);
                        for (int i = 0; i < HashSize; i++)
                        {
                            if (hashBytes[i + SaltSize] != testHash[i])
                                return false;
                        }
                        return true;
                    }
                }
                // Fall back to old SHA-256 verification for backward compatibility
                else if (hashBytes.Length == 32)
                {
                    using (var sha256 = SHA256.Create())
                    {
                        var bytes = Encoding.UTF8.GetBytes(password);
                        var testHash = sha256.ComputeHash(bytes);
                        var testHashBase64 = Convert.ToBase64String(testHash);
                        return testHashBase64 == hash;
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public class SessionToken
    {
        public string Token { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string IpAddress { get; set; }
        
        public bool IsValid()
        {
            return DateTime.UtcNow < ExpiresAt;
        }
    }

    public class RateLimitEntry
    {
        public int RequestCount { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime? BlockedUntil { get; set; }
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
        public string ListenAddress { get; set; } = "localhost";
        public string AllowedCorsOrigins { get; set; } = "";
        public string AdminPasswordHash { get; set; }
        public string EmbyApiKey { get; set; }
        public string StrmTargetLibraryId { get; set; }
        public string StrmTargetLibraryName { get; set; }
        public string ExternalServerUrl { get; set; }
        public int SessionExpirationMinutes { get; set; } = 60;
        public int RateLimitRequestsPerMinute { get; set; } = 60;
        public int RateLimitBlockDurationMinutes { get; set; } = 15;

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
            ListenAddress = "localhost";
            AllowedCorsOrigins = "";
            AdminPasswordHash = string.Empty;
            ExternalServerUrl = string.Empty;
            SessionExpirationMinutes = 60;
            RateLimitRequestsPerMinute = 60;
            RateLimitBlockDurationMinutes = 15;
        }
    }
}
