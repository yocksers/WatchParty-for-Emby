define(['./api', 'loading', 'toast'], function (api, loading, toast) {
    'use strict';

    class PartyManager {
        constructor() {
            this.api = api;
        }

        async createParty(partyData) {
            loading.show();
            try {
                const config = await this.api.getPluginConfiguration();
                
                const newParty = {
                    Id: this.generateGuid(),
                    LibraryId: partyData.libraryId,
                    ItemId: partyData.itemId,
                    ItemName: partyData.itemName,
                    ItemType: partyData.itemType,
                    SeriesId: partyData.seriesId,
                    SeasonId: partyData.seasonId,
                    CollectionName: partyData.collectionName,
                    TargetLibraryId: partyData.targetLibraryId,
                    TargetLibraryPath: partyData.targetLibraryPath,
                    IsActive: partyData.isActive || false,
                    MaxParticipants: parseInt(partyData.maxParticipants) || 50,
                    AllowedUserIds: partyData.allowedUserIds || [],
                    IsWaitingRoom: partyData.isWaitingRoom || false,
                    AutoStartWhenReady: partyData.autoStartWhenReady || false,
                    MinReadyCount: parseInt(partyData.minReadyCount) || 1,
                    PauseControl: partyData.pauseControl || 'Anyone',
                    HostOnlySeek: partyData.hostOnlySeek || false,
                    LockSeekAhead: partyData.lockSeekAhead || false,
                    SyncToleranceSeconds: parseInt(partyData.syncToleranceSeconds) || 10,
                    MaxBufferThresholdSeconds: parseInt(partyData.maxBufferThresholdSeconds) || 30,
                    AutoKickInactiveMinutes: partyData.autoKickInactiveMinutes || false,
                    InactiveTimeoutMinutes: parseInt(partyData.inactiveTimeoutMinutes) || 15,
                    CurrentPositionTicks: 0,
                    IsPlaying: false,
                    CreatedDate: new Date().toISOString()
                };

                config.WatchParties.push(newParty);
                
                const result = await this.api.updatePluginConfiguration(config);
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
                toast('Watch party created successfully!');
                return newParty;
            } catch (error) {
                loading.hide();
                toast({ type: 'error', text: 'Error creating watch party: ' + error.message });
                throw error;
            }
        }

        async deleteParty(partyId) {
            if (!confirm('Are you sure you want to delete this watch party?')) {
                return false;
            }

            loading.show();
            try {
                const config = await this.api.getPluginConfiguration();
                config.WatchParties = config.WatchParties.filter(p => p.Id !== partyId);
                
                const result = await this.api.updatePluginConfiguration(config);
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
                toast('Watch party deleted.');
                return true;
            } catch (error) {
                loading.hide();
                toast({ type: 'error', text: 'Error deleting party: ' + error.message });
                return false;
            }
        }

        async toggleParty(partyId) {
            loading.show();
            try {
                const config = await this.api.getPluginConfiguration();
                const party = config.WatchParties.find(p => p.Id === partyId);
                
                if (party) {
                    party.IsActive = !party.IsActive;
                    
                    const result = await this.api.updatePluginConfiguration(config);
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                    toast(party.IsActive ? 'Party activated.' : 'Party deactivated.');
                    return party;
                }
                
                loading.hide();
                return null;
            } catch (error) {
                loading.hide();
                toast({ type: 'error', text: 'Error updating party: ' + error.message });
                return null;
            }
        }

        async updateGlobalSettings(settings) {
            loading.show();
            try {
                const config = await this.api.getPluginConfiguration();
                
                config.SyncIntervalSeconds = parseInt(settings.syncIntervalSeconds) || 5;
                config.SyncOffsetMilliseconds = parseInt(settings.syncOffsetMilliseconds) || 1000;
                config.EnableDebugLogging = settings.enableDebugLogging || false;
                config.WatchPartyStrmPath = settings.watchPartyStrmPath || '';
                
                const result = await this.api.updatePluginConfiguration(config);
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
                toast('Settings saved successfully!');
                return true;
            } catch (error) {
                loading.hide();
                toast({ type: 'error', text: 'Error saving settings: ' + error.message });
                return false;
            }
        }

        generateGuid() {
            return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                const r = Math.random() * 16 | 0;
                const v = c === 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            });
        }

        validatePartyData(data) {
            const errors = [];

            if (!data.itemId) {
                errors.push('Please select content to watch');
            }

            if (!data.targetLibraryId) {
                errors.push('Please select a target library');
            }

            if (data.maxParticipants < 2 || data.maxParticipants > 100) {
                errors.push('Maximum participants must be between 2 and 100');
            }

            if (data.syncToleranceSeconds < 1 || data.syncToleranceSeconds > 60) {
                errors.push('Sync tolerance must be between 1 and 60 seconds');
            }

            return errors;
        }
    }

    return new PartyManager();
});
