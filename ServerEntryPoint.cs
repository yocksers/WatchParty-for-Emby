using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace WatchPartyForEmby
{
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly Plugin _plugin;
        private Timer _syncTimer;
        private readonly Dictionary<string, HashSet<string>> _partySyncedSessions = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, Dictionary<string, bool>> _partySessionPauseState = new Dictionary<string, Dictionary<string, bool>>();
        private readonly Dictionary<string, string> _partyHostSessions = new Dictionary<string, string>();
        private readonly HashSet<string> _trackedPartyIds = new HashSet<string>();
        private readonly Dictionary<string, Dictionary<string, PartyParticipant>> _partyParticipants = new Dictionary<string, Dictionary<string, PartyParticipant>>();
        private readonly Dictionary<string, HashSet<string>> _partyReadyUsers = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, Dictionary<string, int>> _partyPauseVotes = new Dictionary<string, Dictionary<string, int>>();
        private readonly Dictionary<string, string> _partyLibraryPathCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _partyStrmPathCache = new Dictionary<string, string>();

        public ServerEntryPoint(
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILibraryMonitor libraryMonitor,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger(GetType().Name);
            _plugin = Plugin.Instance;
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    fullPath = fullPath.ToLowerInvariant();
                }
                
                return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Replace('\\', Path.DirectorySeparatorChar)
                          .Replace('/', Path.DirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private StringComparison GetPathComparison()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        private StringComparer GetPathComparer()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        public void Run()
        {
            _logger.Info("Watch Party plugin started");
            
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            
            _plugin.ConfigurationUpdated += OnConfigurationUpdated;
            
            MigrateLegacyConfiguration();
            
            var config = _plugin.Configuration;
            foreach (var party in config.WatchParties)
            {
                _trackedPartyIds.Add(party.Id);
            }
            
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer = new Timer(CheckAndSyncUsers, null, intervalMs, intervalMs);
            
            Task.Run(() => CreateAllWatchPartyStrmFiles());
        }

        private void MigrateLegacyConfiguration()
        {
            var config = _plugin.Configuration;
            
            if (!string.IsNullOrEmpty(config.SelectedItemId) && config.WatchParties.Count == 0)
            {
                _logger.Info("Migrating legacy single-party configuration");
                
                var legacyParty = new WatchPartyItem
                {
                    LibraryId = config.SelectedLibraryId,
                    ItemId = config.SelectedItemId,
                    ItemName = config.SelectedItemName,
                    ItemType = config.SelectedItemType,
                    SeriesId = config.SelectedSeriesId,
                    SeasonId = config.SelectedSeasonId,
                    CollectionName = config.CollectionName ?? "Watch Party",
                    IsActive = config.IsPartyActive,
                    CurrentPositionTicks = config.CurrentPositionTicks,
                    IsPlaying = config.IsPlaying,
                    MaxParticipants = config.MaxParticipants
                };
                
                config.WatchParties.Add(legacyParty);
                _plugin.SaveConfiguration();
                
                _logger.Info($"Migrated legacy party: {legacyParty.ItemName}");
            }
        }

        private void OnConfigurationUpdated(object sender, EventArgs e)
        {
            _logger.Info("Configuration updated, refreshing collections and timer");
            
            var config = _plugin.Configuration;
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer?.Change(intervalMs, intervalMs);
            
            Task.Run(() => CleanupRemovedParties());
            Task.Run(() => ValidateAndCleanWatchParties());
            Task.Run(() => CreateAllWatchPartyStrmFiles());
        }

        private PartyParticipant GetOrCreateParticipant(string partyId, SessionInfo session)
        {
            if (!_partyParticipants.ContainsKey(partyId))
            {
                _partyParticipants[partyId] = new Dictionary<string, PartyParticipant>();
            }

            if (!_partyParticipants[partyId].ContainsKey(session.UserId))
            {
                var user = session.UserName ?? session.UserId;
                _partyParticipants[partyId][session.UserId] = new PartyParticipant
                {
                    UserId = session.UserId,
                    UserName = user,
                    SessionId = session.Id
                };
                _logger.Info($"[Party {partyId}] New participant: {user} ({session.UserId})");
            }

            return _partyParticipants[partyId][session.UserId];
        }

        private void UpdateParticipantActivity(string partyId, string userId, long positionTicks, bool isPaused)
        {
            if (_partyParticipants.ContainsKey(partyId) && _partyParticipants[partyId].ContainsKey(userId))
            {
                var participant = _partyParticipants[partyId][userId];
                participant.LastActivityAt = DateTime.UtcNow;
                participant.CurrentPositionTicks = positionTicks;
                participant.IsPaused = isPaused;
            }
        }

        private void RemoveParticipant(string partyId, string userId)
        {
            if (_partyParticipants.ContainsKey(partyId) && _partyParticipants[partyId].ContainsKey(userId))
            {
                var participant = _partyParticipants[partyId][userId];
                _logger.Info($"[Party {partyId}] Participant left: {participant.UserName}");
                _partyParticipants[partyId].Remove(userId);
            }
        }

        private bool CanUserJoinParty(WatchPartyItem party, string userId)
        {
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                if (!party.AllowedUserIds.Contains(userId))
                {
                    _logger.Warn($"[Party {party.Id}] User {userId} not in allowed list");
                    return false;
                }
            }

            if (_partyParticipants.ContainsKey(party.Id))
            {
                var currentCount = _partyParticipants[party.Id].Count;
                if (currentCount >= party.MaxParticipants)
                {
                    _logger.Warn($"[Party {party.Id}] Maximum participants ({party.MaxParticipants}) reached");
                    return false;
                }
            }

            return true;
        }

        private void CheckAndRemoveInactiveParticipants(WatchPartyItem party)
        {
            if (!party.AutoKickInactiveMinutes || !_partyParticipants.ContainsKey(party.Id))
            {
                return;
            }

            var inactiveThreshold = DateTime.UtcNow.AddMinutes(-party.InactiveTimeoutMinutes);
            var inactiveUsers = _partyParticipants[party.Id]
                .Where(kvp => kvp.Value.LastActivityAt < inactiveThreshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var userId in inactiveUsers)
            {
                var participant = _partyParticipants[party.Id][userId];
                _logger.Info($"[Party {party.Id}] Removing inactive user: {participant.UserName} (inactive for {party.InactiveTimeoutMinutes} minutes)");
                RemoveParticipant(party.Id, userId);
                
                if (_partySyncedSessions.ContainsKey(party.Id))
                {
                    _partySyncedSessions[party.Id].RemoveWhere(sessionId => 
                    {
                        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
                        return session?.UserId == userId;
                    });
                }
            }
        }

        private List<PartyParticipant> GetParticipants(string partyId)
        {
            if (_partyParticipants.ContainsKey(partyId))
            {
                return _partyParticipants[partyId].Values.ToList();
            }
            return new List<PartyParticipant>();
        }

        private async Task CheckWaitingRoomReadiness(WatchPartyItem party)
        {
            if (!party.IsWaitingRoom || party.IsPlaying)
            {
                _logger.Debug($"[Party {party.Id}] Skipping waiting room check - IsWaitingRoom: {party.IsWaitingRoom}, IsPlaying: {party.IsPlaying}");
                return;
            }

            if (!_partyReadyUsers.ContainsKey(party.Id))
            {
                _partyReadyUsers[party.Id] = new HashSet<string>();
            }

            var readyCount = _partyReadyUsers[party.Id].Count;
            
            int expectedUsers;
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                expectedUsers = party.AllowedUserIds.Count;
                _logger.Info($"[Party {party.Id}] Waiting room: {readyCount}/{expectedUsers} allowed users ready");
            }
            else
            {
                expectedUsers = _partyParticipants.ContainsKey(party.Id) ? _partyParticipants[party.Id].Count : 0;
                _logger.Info($"[Party {party.Id}] Waiting room: {readyCount}/{expectedUsers} participants ready");
            }

            _logger.Info($"[Party {party.Id}] AutoStartWhenReady={party.AutoStartWhenReady}, MinReadyCount={party.MinReadyCount}, readyCount={readyCount}, expectedUsers={expectedUsers}");

            if (party.AutoStartWhenReady && readyCount >= party.MinReadyCount)
            {
                if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
                {
                    _logger.Info($"[Party {party.Id}] Checking whitelist readiness: {readyCount} >= {party.AllowedUserIds.Count}");
                    if (readyCount >= party.AllowedUserIds.Count)
                    {
                        _logger.Info($"[Party {party.Id}] All whitelisted users ready, starting party!");
                        await StartPartyFromWaitingRoom(party);
                    }
                }
                else if (expectedUsers > 0 && readyCount >= expectedUsers)
                {
                    _logger.Info($"[Party {party.Id}] All participants ready ({readyCount}/{expectedUsers}), starting party!");
                    await StartPartyFromWaitingRoom(party);
                }
                else
                {
                    _logger.Info($"[Party {party.Id}] Not all participants ready yet: {readyCount}/{expectedUsers}");
                }
            }
            else
            {
                _logger.Info($"[Party {party.Id}] Auto-start condition not met - AutoStartWhenReady={party.AutoStartWhenReady}, readyCount ({readyCount}) >= MinReadyCount ({party.MinReadyCount}): {readyCount >= party.MinReadyCount}");
            }
        }

        private async Task StartPartyFromWaitingRoom(WatchPartyItem party)
        {
            party.IsWaitingRoom = false;
            party.IsPlaying = true;
            _plugin.SaveConfiguration();

            var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
            foreach (var session in sessions)
            {
                if (_partyParticipants.ContainsKey(party.Id) && _partyParticipants[party.Id].ContainsKey(session.UserId))
                {
                    try
                    {
                        await _sessionManager.SendPlaystateCommand(session.Id, session.Id, new PlaystateRequest
                        {
                            Command = PlaystateCommand.Unpause
                        }, CancellationToken.None);
                        _logger.Info($"[Party {party.Id}] Started playback for {session.UserName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[Party {party.Id}] Error starting playback for {session.UserName}", ex);
                    }
                }
            }
        }

        private async Task HandlePauseAttempt(WatchPartyItem party, SessionInfo session, bool isHost)
        {
            if (party.PauseControl == "Anyone")
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} paused (Anyone mode), pausing all other users");
                
                // Pause all other users in the party
                await PauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && !isHost)
            {
                _logger.Warn($"[Party {party.Id}] {session.UserName} tried to pause (Host-only mode), unpausing");
                await _sessionManager.SendPlaystateCommand(session.Id, session.Id, new PlaystateRequest
                {
                    Command = PlaystateCommand.Unpause
                }, CancellationToken.None);
                return;
            }
            
            if (party.PauseControl == "Host" && isHost)
            {
                _logger.Info($"[Party {party.Id}] Host {session.UserName} paused, pausing all other users");
                await PauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Vote")
            {
                if (!_partyPauseVotes.ContainsKey(party.Id))
                {
                    _partyPauseVotes[party.Id] = new Dictionary<string, int>();
                }
                
                _partyPauseVotes[party.Id][session.UserId] = 1;
                
                var totalParticipants = _partyParticipants.ContainsKey(party.Id) ? _partyParticipants[party.Id].Count : 1;
                var pauseVotes = _partyPauseVotes[party.Id].Count;
                var requiredVotes = (int)Math.Ceiling(totalParticipants / 2.0);
                
                _logger.Info($"[Party {party.Id}] Pause vote: {pauseVotes}/{requiredVotes} votes (total: {totalParticipants})");
                
                if (pauseVotes >= requiredVotes)
                {
                    _logger.Info($"[Party {party.Id}] Pause vote passed, pausing all users");
                    await PauseAllUsers(party, null);
                }
                else
                {
                    _logger.Info($"[Party {party.Id}] Not enough votes, unpausing {session.UserName}");
                    await _sessionManager.SendPlaystateCommand(session.Id, session.Id, new PlaystateRequest
                    {
                        Command = PlaystateCommand.Unpause
                    }, CancellationToken.None);
                }
            }
        }
        
        private async Task PauseAllUsers(WatchPartyItem party, string excludeSessionId)
        {
            try
            {
                var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                
                foreach (var otherSession in sessions)
                {
                    // Skip the session that initiated the pause
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }
                    
                    var item = _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);
                    
                    if (matchingParty != null && matchingParty.Id == party.Id)
                    {
                        _logger.Info($"[Party {party.Id}] Pausing user {otherSession.UserName} (Session: {otherSession.Id})");
                        try
                        {
                            await _sessionManager.SendPlaystateCommand(otherSession.Id, otherSession.Id, new PlaystateRequest
                            {
                                Command = PlaystateCommand.Pause
                            }, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException($"[Party {party.Id}] Error pausing user {otherSession.UserName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Party {party.Id}] Error pausing all users", ex);
            }
        }
        
        private async Task HandleUnpauseAttempt(WatchPartyItem party, SessionInfo session, bool isHost)
        {
            if (party.PauseControl == "Anyone")
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} unpaused (Anyone mode), unpausing all other users");
                await UnpauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && isHost)
            {
                _logger.Info($"[Party {party.Id}] Host {session.UserName} unpaused, unpausing all other users");
                await UnpauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && !isHost)
            {
                // Non-host tried to unpause in Host-only mode, re-pause them
                _logger.Warn($"[Party {party.Id}] {session.UserName} tried to unpause (Host-only mode), re-pausing");
                await _sessionManager.SendPlaystateCommand(session.Id, session.Id, new PlaystateRequest
                {
                    Command = PlaystateCommand.Pause
                }, CancellationToken.None);
                return;
            }
            
            if (party.PauseControl == "Vote")
            {
                // Clear pause votes when someone unpauses
                if (_partyPauseVotes.ContainsKey(party.Id))
                {
                    _partyPauseVotes[party.Id].Clear();
                }
                
                _logger.Info($"[Party {party.Id}] {session.UserName} unpaused, clearing pause votes and unpausing all users");
                await UnpauseAllUsers(party, session.Id);
            }
        }
        
        private async Task UnpauseAllUsers(WatchPartyItem party, string excludeSessionId)
        {
            try
            {
                var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                
                foreach (var otherSession in sessions)
                {
                    // Skip the session that initiated the unpause
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }
                    
                    var item = _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);
                    
                    if (matchingParty != null && matchingParty.Id == party.Id)
                    {
                        _logger.Info($"[Party {party.Id}] Unpausing user {otherSession.UserName} (Session: {otherSession.Id})");
                        try
                        {
                            await _sessionManager.SendPlaystateCommand(otherSession.Id, otherSession.Id, new PlaystateRequest
                            {
                                Command = PlaystateCommand.Unpause
                            }, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException($"[Party {party.Id}] Error unpausing user {otherSession.UserName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Party {party.Id}] Error unpausing all users", ex);
            }
        }

        private async Task HandleSeekRestrictions(WatchPartyItem party, SessionInfo session, long currentPosition, bool isHost)
        {
            if (!party.HostOnlySeek && !party.LockSeekAhead)
            {
                return;
            }
            
            if (isHost)
            {
                return;
            }
            
            var partyPosition = party.CurrentPositionTicks;
            
            if (party.LockSeekAhead && currentPosition > partyPosition)
            {
                var difference = TimeSpan.FromTicks(currentPosition - partyPosition).TotalSeconds;
                if (difference > party.SyncToleranceSeconds)
                {
                    _logger.Warn($"[Party {party.Id}] {session.UserName} seeked ahead {difference:F1}s, syncing back");
                    var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                    if (item != null)
                    {
                        await SyncUserToPosition(session, item, partyPosition);
                    }
                }
            }
            
            if (party.HostOnlySeek)
            {
                var difference = Math.Abs(currentPosition - partyPosition);
                var differenceSeconds = TimeSpan.FromTicks(difference).TotalSeconds;
                
                if (differenceSeconds > party.SyncToleranceSeconds)
                {
                    _logger.Warn($"[Party {party.Id}] {session.UserName} attempted seek (Host-only mode), syncing back");
                    var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                    if (item != null)
                    {
                        await SyncUserToPosition(session, item, partyPosition);
                    }
                }
            }
        }

        private void ValidateAndCleanWatchParties()
        {
            try
            {
                var config = _plugin.Configuration;
                bool configChanged = false;

                if (string.IsNullOrEmpty(config.WatchPartyStrmPath))
                {
                    return;
                }

                foreach (var party in config.WatchParties.ToList())
                {
                    var partyItem = _libraryManager.GetItemById(party.ItemId);
                    
                    if (partyItem == null)
                    {
                        _logger.Info($"Party {party.Id}: Item {party.ItemName} no longer exists in library, removing from settings");
                        config.WatchParties.Remove(party);
                        configChanged = true;
                        
                        var strmPath = GetStrmFilePath(party);
                        if (File.Exists(strmPath))
                        {
                            File.Delete(strmPath);
                            _logger.Info($"Deleted STRM file: {strmPath}");
                            
                            // Notify Emby about the file system change
                            _libraryMonitor.ReportFileSystemChanged(strmPath);
                            _logger.Info($"Notified Emby about STRM file deletion: {strmPath}");
                        }
                    }
                }

                if (configChanged)
                {
                    _logger.Info("Watch party configuration changed, saving updates");
                    _plugin.SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error validating watch parties", ex);
            }
        }

        private void CleanupRemovedParties()
        {
            try
            {
                var config = _plugin.Configuration;
                var currentPartyIds = new HashSet<string>(config.WatchParties.Select(p => p.Id));
                var removedPartyIds = _trackedPartyIds.Except(currentPartyIds).ToList();
                
                var librariesToClean = new HashSet<string>(GetPathComparer());
                foreach (var removedId in removedPartyIds)
                {
                    _logger.Info($"Party {removedId} was removed, cleaning up...");
                    
                    if (_partyLibraryPathCache.TryGetValue(removedId, out var libPath))
                    {
                        librariesToClean.Add(NormalizePath(libPath));
                    }
                    
                    _partySyncedSessions.Remove(removedId);
                    _partySessionPauseState.Remove(removedId);
                    _partyHostSessions.Remove(removedId);
                }
                
                var removedCacheIds = _partyLibraryPathCache.Keys.Except(currentPartyIds).ToList();
                foreach (var removedId in removedCacheIds)
                {
                    _partyLibraryPathCache.Remove(removedId);
                    _partyStrmPathCache.Remove(removedId);
                }

                var activePaths = config.WatchParties
                    .Select(p => GetStrmFilePath(p))
                    .Where(path => path != null)
                    .Select(path => NormalizePath(path))
                    .ToHashSet(GetPathComparer());

                var librariesWithParties = config.WatchParties
                    .Select(p => GetLibraryPath(p))
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => NormalizePath(path));
                
                foreach (var libPath in librariesWithParties)
                {
                    librariesToClean.Add(libPath);
                }

                foreach (var libraryPath in librariesToClean)
                {
                    if (!Directory.Exists(libraryPath))
                    {
                        _logger.Warn($"Library path does not exist: {libraryPath}");
                        continue;
                    }

                    _logger.Debug($"Checking library for orphaned STRM files: {libraryPath}");
                    
                    foreach (var strmFile in Directory.GetFiles(libraryPath, "*.strm"))
                    {
                        var normalizedStrmFile = NormalizePath(strmFile);
                        if (!activePaths.Contains(normalizedStrmFile))
                        {
                            try
                            {
                                File.Delete(strmFile);
                                _logger.Info($"Deleted orphaned STRM file: {strmFile}");
                                
                                // Notify Emby about the file system change
                                _libraryMonitor.ReportFileSystemChanged(strmFile);
                                _logger.Info($"Notified Emby about STRM file deletion: {strmFile}");
                            }
                            catch (Exception ex)
                            {
                                _logger.ErrorException($"Error deleting STRM file: {strmFile}", ex);
                            }
                        }
                    }
                }
                
                _trackedPartyIds.Clear();
                _trackedPartyIds.UnionWith(currentPartyIds);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cleaning up removed parties", ex);
            }
        }

        private async Task CreateAllWatchPartyStrmFiles()
        {
            var config = _plugin.Configuration;
            
            foreach (var party in config.WatchParties)
            {
                await CreateWatchPartyStrmFile(party);
            }
            
            _logger.Info("Finished creating STRM files for all watch parties");
        }

        private string GetLibraryPath(WatchPartyItem party)
        {
            if (_partyLibraryPathCache.TryGetValue(party.Id, out var cachedPath))
            {
                return cachedPath;
            }

            _logger.Debug($"GetLibraryPath: Starting lookup for party {party.Id}");
            _logger.Debug($"  TargetLibraryPath: '{party.TargetLibraryPath}'");
            _logger.Debug($"  TargetLibraryId: '{party.TargetLibraryId}'");
            _logger.Debug($"  CollectionName: '{party.CollectionName}'");

            if (!string.IsNullOrEmpty(party.TargetLibraryPath) && Directory.Exists(party.TargetLibraryPath))
            {
                _logger.Debug($"GetLibraryPath: Using TargetLibraryPath: {party.TargetLibraryPath}");
                _partyLibraryPathCache[party.Id] = party.TargetLibraryPath;
                return party.TargetLibraryPath;
            }

            try
            {
                var virtualFolders = _libraryManager.GetVirtualFolders();
                _logger.Debug($"GetLibraryPath: Found {virtualFolders.Count} virtual folders");

                if (!string.IsNullOrEmpty(party.TargetLibraryId))
                {
                    _logger.Debug($"GetLibraryPath: Looking for library with ID: {party.TargetLibraryId}");
                    
                    foreach (var vf in virtualFolders)
                    {
                        _logger.Debug($"  Checking virtual folder: Name='{vf.Name}', ItemId='{vf.ItemId}', Locations={vf.Locations?.Length ?? 0}");
                        
                        if (vf.ItemId == party.TargetLibraryId && vf.Locations != null && vf.Locations.Length > 0)
                        {
                            var path = vf.Locations[0];
                            _logger.Debug($"GetLibraryPath: Found library path by ID: {path}");
                            _partyLibraryPathCache[party.Id] = path;
                            return path;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(party.CollectionName))
                {
                    _logger.Debug($"GetLibraryPath: Looking for library with name: {party.CollectionName}");
                    
                    foreach (var vf in virtualFolders)
                    {
                        if (vf.Name.Equals(party.CollectionName, StringComparison.OrdinalIgnoreCase) 
                            && vf.Locations != null && vf.Locations.Length > 0)
                        {
                            var path = vf.Locations[0];
                            _logger.Debug($"GetLibraryPath: Found library path by name: {path}");
                            _partyLibraryPathCache[party.Id] = path;
                            return path;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("GetLibraryPath: Error getting virtual folders", ex);
            }

            _logger.Warn($"GetLibraryPath: Could not find library path for party {party.Id}");
            return null;
        }

        private async Task CreateWatchPartyStrmFile(WatchPartyItem party)
        {
            try
            {
                var libraryPath = GetLibraryPath(party);
                if (string.IsNullOrEmpty(libraryPath))
                {
                    _logger.Warn($"Party {party.Id}: Could not determine library path, skipping STRM file creation");
                    return;
                }
                
                if (string.IsNullOrEmpty(party.ItemId))
                {
                    _logger.Info($"Party {party.Id}: No content selected, skipping STRM file creation");
                    return;
                }

                var item = _libraryManager.GetItemById(party.ItemId);
                if (item == null)
                {
                    _logger.Warn($"Party {party.Id}: Selected item ID {party.ItemId} not found");
                    return;
                }

                var strmPath = GetStrmFilePath(party);
                var itemPath = item.Path;
                
                if (string.IsNullOrEmpty(itemPath))
                {
                    _logger.Warn($"Party {party.Id}: Item {item.Name} has no path");
                    return;
                }

                await File.WriteAllTextAsync(strmPath, itemPath);
                _logger.Info($"Created STRM file for party {party.Id}: {strmPath}");
                
                // Notify Emby's LibraryMonitor about the new file
                _libraryMonitor.ReportFileSystemChanged(strmPath);
                _logger.Info($"Notified Emby about new STRM file: {strmPath}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error creating STRM file for party {party.Id}", ex);
            }
        }

        private string GetStrmFilePath(WatchPartyItem party)
        {
            if (_partyStrmPathCache.TryGetValue(party.Id, out var cachedStrmPath))
            {
                return cachedStrmPath;
            }

            var libraryPath = GetLibraryPath(party);
            if (string.IsNullOrEmpty(libraryPath))
            {
                return null;
            }

            var item = _libraryManager.GetItemById(party.ItemId);
            
            string strmPath;
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                var originalFileName = Path.GetFileNameWithoutExtension(item.Path);
                strmPath = Path.Combine(libraryPath, $"{originalFileName}.strm");
            }
            else
            {
                var sanitizedName = string.Join("_", party.ItemName.Split(Path.GetInvalidFileNameChars()));
                strmPath = Path.Combine(libraryPath, $"{sanitizedName}.strm");
            }

            _partyStrmPathCache[party.Id] = strmPath;
            return strmPath;
        }

        private async void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStart event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, UserId: {e.Session?.UserId}");
                
                var party = FindPartyForItem(e.Item);
                
                if (party != null && party.IsActive)
                {
                    if (!CanUserJoinParty(party, e.Session.UserId))
                    {
                        _logger.Warn($"[Party {party.Id}] Access denied for user {e.Session.UserId}");
                        await _sessionManager.SendPlaystateCommand(e.Session.Id, e.Session.Id, new PlaystateRequest
                        {
                            Command = PlaystateCommand.Stop
                        }, CancellationToken.None);
                        return;
                    }

                    _logger.Info($"[Watch Party] User {e.Session.UserId} started watching party content: {party.ItemName}");
                    
                    if (!_partySyncedSessions.ContainsKey(party.Id))
                    {
                        _partySyncedSessions[party.Id] = new HashSet<string>();
                    }
                    if (!_partySessionPauseState.ContainsKey(party.Id))
                    {
                        _partySessionPauseState[party.Id] = new Dictionary<string, bool>();
                    }
                    
                    var syncedSessions = _partySyncedSessions[party.Id];
                    var pauseState = _partySessionPauseState[party.Id];
                    
                    var participant = GetOrCreateParticipant(party.Id, e.Session);
                    UpdateParticipantActivity(party.Id, e.Session.UserId, e.PlaybackPositionTicks ?? 0, false);
                    
                    var userStartPosition = e.PlaybackPositionTicks ?? 0;
                    var isInSyncedSet = syncedSessions.Contains(e.Session.Id);
                    
                    // Watch parties should always start from the beginning, not from a resume position
                    // Only sync to party position if the user is resuming from a pause within the party
                    var wasPaused = pauseState.TryGetValue(e.Session.Id, out var isPaused) && isPaused;
                    
                    if (!isInSyncedSet && userStartPosition > 0 && !wasPaused)
                    {
                        // User is starting for the first time with a resume position - reset to beginning or party position
                        var targetPosition = party.CurrentPositionTicks;
                        _logger.Info($"[Party {party.Id}] User {e.Session.UserId} started with resume position {TimeSpan.FromTicks(userStartPosition).TotalSeconds:F1}s, resetting to party position {TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s");
                        await SyncUserToPosition(e.Session, e.Item, targetPosition);
                    }
                    
                    if (party.IsWaitingRoom && !party.IsPlaying)
                    {
                        _logger.Info($"[Party {party.Id}] User {e.Session.UserId} started playback - marking as ready");
                        
                        if (!_partyReadyUsers.ContainsKey(party.Id))
                        {
                            _partyReadyUsers[party.Id] = new HashSet<string>();
                        }
                        _partyReadyUsers[party.Id].Add(e.Session.UserId);
                        
                        _logger.Info($"[Party {party.Id}] Pausing user in waiting room");
                        await _sessionManager.SendPlaystateCommand(e.Session.Id, e.Session.Id, new PlaystateRequest
                        {
                            Command = PlaystateCommand.Pause
                        }, CancellationToken.None);
                        
                        await CheckWaitingRoomReadiness(party);
                        return;
                    }
                    
                    if (wasPaused)
                    {
                        _logger.Info($"[Watch Party] User {e.Session.UserId} resumed from pause, forcing sync");
                        pauseState[e.Session.Id] = false;
                        await SyncUserToPosition(e.Session, e.Item, party.CurrentPositionTicks);
                        return;
                    }
                    
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;
                    
                    _logger.Info($"[Watch Party] Session {e.Session.Id} is in synced set: {isInSyncedSet}, IsMaster: {isMaster}");
                    
                    if (!isInSyncedSet)
                    {
                        syncedSessions.Add(e.Session.Id);
                        _logger.Info($"[Watch Party] Added session {e.Session.Id} to synced set");
                        
                        if (!isMaster && party.CurrentPositionTicks > 0)
                        {
                            _logger.Info($"[Watch Party] First-time sync for session {e.Session.Id} (user at {TimeSpan.FromTicks(userStartPosition).TotalSeconds:F1}s, party at {TimeSpan.FromTicks(party.CurrentPositionTicks).TotalSeconds:F1}s)");
                            await SyncUserToPosition(e.Session, e.Item, party.CurrentPositionTicks);
                        }
                        else if (isMaster)
                        {
                            _logger.Info($"[Watch Party] Session {e.Session.Id} is the master, not syncing");
                        }
                    }
                    else
                    {
                        _logger.Info($"[Watch Party] Session {e.Session.Id} already synced, skipping initial sync");
                    }
                }
                else
                {
                    _logger.Debug($"[Watch Party] Not a watch party item or party not active");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback start", ex);
            }
        }

        private void CheckAndSyncUsers(object state)
        {
            try
            {
                ValidateAndCleanWatchParties();
                
                var config = _plugin.Configuration;
                
                foreach (var party in config.WatchParties.Where(p => p.IsActive))
                {
                    CheckAndRemoveInactiveParticipants(party);
                    
                    if (party.CurrentPositionTicks == 0 && !party.IsWaitingRoom)
                    {
                        continue;
                    }
                    
                    _logger.Debug($"[Party {party.Id}] Periodic sync check at position {party.CurrentPositionTicks} ticks");
                    
                    var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                    var syncThreshold = TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks;
                    var maxBufferThreshold = TimeSpan.FromSeconds(party.MaxBufferThresholdSeconds).Ticks;
                    
                    foreach (var session in sessions)
                    {
                        var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        var matchingParty = FindPartyForItem(item);
                        
                        if (matchingParty != null && matchingParty.Id == party.Id)
                        {
                            var currentPosition = session.PlayState?.PositionTicks ?? 0;
                            var positionDifference = Math.Abs(currentPosition - party.CurrentPositionTicks);
                            
                            UpdateParticipantActivity(party.Id, session.UserId, currentPosition, session.PlayState?.IsPaused ?? false);
                            
                            if (positionDifference > maxBufferThreshold && currentPosition < party.CurrentPositionTicks)
                            {
                                _logger.Warn($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind (exceeds buffer threshold)");
                                
                                if (_partyParticipants.ContainsKey(party.Id) && _partyParticipants[party.Id].ContainsKey(session.UserId))
                                {
                                    _partyParticipants[party.Id][session.UserId].IsBuffering = true;
                                }
                            }
                            
                            if (positionDifference > syncThreshold)
                            {
                                _logger.Info($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s out of sync, syncing");
                                Task.Run(async () => await SyncUserToPosition(session, item, party.CurrentPositionTicks));
                            }
                            else
                            {
                                _logger.Debug($"[Party {party.Id}] Session {session.Id} is in sync (diff: {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s)");
                                
                                if (_partyParticipants.ContainsKey(party.Id) && _partyParticipants[party.Id].ContainsKey(session.UserId))
                                {
                                    _partyParticipants[party.Id][session.UserId].IsBuffering = false;
                                }
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(config.SelectedItemId) && config.IsPartyActive && config.CurrentPositionTicks > 0)
                {
                    var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                    
                    foreach (var session in sessions)
                    {
                        var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        if (item != null && IsSelectedItem(item, config))
                        {
                            var currentPosition = session.PlayState?.PositionTicks ?? 0;
                            var positionDifference = Math.Abs(currentPosition - config.CurrentPositionTicks);
                            var syncThreshold = TimeSpan.FromSeconds(10).Ticks;
                            
                            if (positionDifference > syncThreshold)
                            {
                                _logger.Info($"[Watch Party] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind, syncing");
                                Task.Run(async () => await SyncUserToPosition(session, item, config.CurrentPositionTicks));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error in periodic sync check", ex);
            }
        }

        private async Task SyncUserToPosition(SessionInfo session, BaseItem item, long positionTicks)
        {
            try
            {
                var config = _plugin.Configuration;
                
                var offsetTicks = TimeSpan.FromMilliseconds(config.SyncOffsetMilliseconds).Ticks;
                var adjustedPosition = positionTicks + offsetTicks;
                
                _logger.Info($"[Watch Party] Syncing session {session.Id} to position {positionTicks} ticks (adjusted: {adjustedPosition} with {config.SyncOffsetMilliseconds:+#;-#;0}ms offset)");
                
                await _sessionManager.SendPlaystateCommand(
                    session.Id,
                    session.Id,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        SeekPositionTicks = adjustedPosition
                    },
                    CancellationToken.None);
                    
                _logger.Info($"[Watch Party] Seek command sent successfully to session {session.Id}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Watch Party] Error syncing session {session.Id}", ex);
            }
        }

        private WatchPartyItem FindPartyForItem(BaseItem item)
        {
            if (item == null) return null;
            
            var config = _plugin.Configuration;
            
            // First, check if this is a STRM file
            var isStrmFile = !string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
            
            if (isStrmFile)
            {
                var itemPath = NormalizePath(item.Path);
                _logger.Debug($"[Watch Party] Checking if STRM file {itemPath} belongs to a watch party");
                
                // Method 1: Check by STRM file path
                foreach (var party in config.WatchParties)
                {
                    if (string.IsNullOrEmpty(party.ItemId)) continue;
                    
                    var partyStrmPath = GetStrmFilePath(party);
                    if (!string.IsNullOrEmpty(partyStrmPath))
                    {
                        var normalizedPartyPath = NormalizePath(partyStrmPath);
                        if (string.Equals(itemPath, normalizedPartyPath, GetPathComparison()))
                        {
                            _logger.Debug($"[Watch Party] STRM file path {itemPath} matches party {party.Id}");
                            return party;
                        }
                    }
                }
                
                // Method 2: Check by STRM content (reads the file and matches the target path)
                try
                {
                    if (File.Exists(item.Path))
                    {
                        var strmContent = File.ReadAllText(item.Path).Trim();
                        _logger.Debug($"[Watch Party] STRM file content: {strmContent}");
                        
                        foreach (var party in config.WatchParties)
                        {
                            if (string.IsNullOrEmpty(party.ItemId)) continue;
                            
                            var partyItem = _libraryManager.GetItemById(party.ItemId);
                            if (partyItem != null && !string.IsNullOrEmpty(partyItem.Path))
                            {
                                var normalizedStrmContent = NormalizePath(strmContent);
                                var normalizedPartyItemPath = NormalizePath(partyItem.Path);
                                if (string.Equals(normalizedStrmContent, normalizedPartyItemPath, GetPathComparison()))
                                {
                                    _logger.Debug($"[Watch Party] STRM file content matches party {party.Id} item path");
                                    return party;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error reading STRM file {itemPath}", ex);
                }
            }
            
            // Check by item ID directly
            foreach (var party in config.WatchParties)
            {
                if (string.IsNullOrEmpty(party.ItemId)) continue;
                
                if (Guid.TryParse(party.ItemId, out var partyItemGuid) && item.Id == partyItemGuid)
                {
                    return party;
                }
                else if (long.TryParse(party.ItemId, out var partyInternalId) && item.InternalId == partyInternalId)
                {
                    return party;
                }
            }
            
            if (!string.IsNullOrEmpty(config.SelectedItemId))
            {
                if (Guid.TryParse(config.SelectedItemId, out var selectedItemGuid) && item.Id == selectedItemGuid)
                {
                    return new WatchPartyItem
                    {
                        Id = "legacy",
                        ItemId = config.SelectedItemId,
                        ItemName = config.SelectedItemName,
                        IsActive = config.IsPartyActive,
                        CurrentPositionTicks = config.CurrentPositionTicks,
                        IsPlaying = config.IsPlaying,
                        MaxParticipants = config.MaxParticipants
                    };
                }
                else if (long.TryParse(config.SelectedItemId, out var selectedInternalId) && item.InternalId == selectedInternalId)
                {
                    return new WatchPartyItem
                    {
                        Id = "legacy",
                        ItemId = config.SelectedItemId,
                        ItemName = config.SelectedItemName,
                        IsActive = config.IsPartyActive,
                        CurrentPositionTicks = config.CurrentPositionTicks,
                        IsPlaying = config.IsPlaying,
                        MaxParticipants = config.MaxParticipants
                    };
                }
            }
            
            return null;
        }

        private bool IsSelectedItem(BaseItem item, PluginConfiguration config)
        {
            if (item == null || string.IsNullOrEmpty(config.SelectedItemId))
            {
                return false;
            }
            
            if (Guid.TryParse(config.SelectedItemId, out var selectedItemGuid))
            {
                return item.Id == selectedItemGuid;
            }
            else if (long.TryParse(config.SelectedItemId, out var selectedInternalId))
            {
                return item.InternalId == selectedInternalId;
            }
            
            return false;
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                _logger.Debug($"[Watch Party] PlaybackProgress event fired - Item: {e.Item?.Name}, Position: {e.PlaybackPositionTicks}, Paused: {e.IsPaused}, Session: {e.Session.Id}");
                
                var config = _plugin.Configuration;
                
                var party = FindPartyForItem(e.Item);
                
                if (party != null && party.IsActive)
                {
                    if (!_partySessionPauseState.ContainsKey(party.Id))
                    {
                        _partySessionPauseState[party.Id] = new Dictionary<string, bool>();
                    }
                    
                    var pauseState = _partySessionPauseState[party.Id];
                    var wasPaused = pauseState.TryGetValue(e.Session.Id, out var previousPause) && previousPause;
                    pauseState[e.Session.Id] = e.IsPaused;
                    
                    UpdateParticipantActivity(party.Id, e.Session.UserId, e.PlaybackPositionTicks ?? 0, e.IsPaused);
                    
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;
                    
                    if (e.IsPaused && !wasPaused)
                    {
                        HandlePauseAttempt(party, e.Session, isMaster).Wait();
                    }
                    
                    if (!e.IsPaused && wasPaused)
                    {
                        // User unpaused - resume all other users
                        HandleUnpauseAttempt(party, e.Session, isMaster).Wait();
                    }
                    
                    if (!e.IsPaused)
                    {
                        HandleSeekRestrictions(party, e.Session, e.PlaybackPositionTicks ?? 0, isMaster).Wait();
                    }
                    
                    if (isMaster)
                    {
                        party.CurrentPositionTicks = e.PlaybackPositionTicks ?? 0;
                        party.IsPlaying = !e.IsPaused;
                        _plugin.SaveConfiguration();
                        
                        _logger.Debug($"[Watch Party] Master user updated party {party.Id} position to {party.CurrentPositionTicks} ticks, Playing: {party.IsPlaying}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback progress", ex);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStopped event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, Session: {e.Session.Id}");
                
                var config = _plugin.Configuration;
                
                var party = FindPartyForItem(e.Item);
                
                if (party != null && party.IsActive)
                {
                    RemoveParticipant(party.Id, e.Session.UserId);
                    
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;
                    
                    if (isMaster)
                    {
                        _logger.Info($"[Watch Party] Master user {e.Session.UserId} stopped for party {party.Id} (keeping position)");
                        party.IsPlaying = false;
                        _plugin.SaveConfiguration();
                        
                        if (_partySyncedSessions.ContainsKey(party.Id))
                        {
                            _partySyncedSessions[party.Id].Clear();
                        }
                        if (_partySessionPauseState.ContainsKey(party.Id))
                        {
                            _partySessionPauseState[party.Id].Clear();
                        }
                        if (_partyReadyUsers.ContainsKey(party.Id))
                        {
                            _partyReadyUsers[party.Id].Clear();
                        }
                        
                        if (_partyParticipants.ContainsKey(party.Id) && _partyParticipants[party.Id].Count > 0)
                        {
                            var newHostUserId = _partyParticipants[party.Id].Keys.First();
                            var newHostParticipant = _partyParticipants[party.Id][newHostUserId];
                            party.HostUserId = newHostUserId;
                            _partyHostSessions[party.Id] = newHostParticipant.SessionId;
                            _logger.Info($"[Watch Party] Assigned new host: {newHostParticipant.UserName}");
                            _plugin.SaveConfiguration();
                        }
                    }
                    else
                    {
                        _logger.Info($"[Watch Party] Participant {e.Session.UserId} stopped watching party content");
                        
                        if (_partySyncedSessions.ContainsKey(party.Id))
                        {
                            _partySyncedSessions[party.Id].Remove(e.Session.Id);
                        }
                        if (_partySessionPauseState.ContainsKey(party.Id))
                        {
                            _partySessionPauseState[party.Id].Remove(e.Session.Id);
                        }
                        if (_partyReadyUsers.ContainsKey(party.Id))
                        {
                            _partyReadyUsers[party.Id].Remove(e.Session.UserId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback stop", ex);
            }
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _plugin.ConfigurationUpdated -= OnConfigurationUpdated;
            
            _syncTimer?.Dispose();
            
            _logger.Info("Watch Party plugin stopped");
        }
    }
}
