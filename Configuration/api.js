define([], function () {
    'use strict';

    const pluginId = "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d";

    class WatchPartyApi {
        constructor() {
            this.baseUrl = '';
        }

        getPluginConfiguration() {
            return ApiClient.getPluginConfiguration(pluginId);
        }

        updatePluginConfiguration(config) {
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }

        loadLibraries() {
            return ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'));
        }

        loadLibraryContent(libraryId) {
            return ApiClient.getItems(ApiClient.getCurrentUserId(), {
                ParentId: libraryId,
                Recursive: true,
                IncludeItemTypes: 'Movie,Series',
                SortBy: 'SortName',
                SortOrder: 'Ascending',
                Fields: 'Id,Name,ProductionYear,Type'
            });
        }

        loadSeasons(seriesId) {
            return ApiClient.getSeasons(seriesId, {
                userId: ApiClient.getCurrentUserId(),
                Fields: 'Id,Name,IndexNumber'
            });
        }

        loadEpisodes(seriesId, seasonId) {
            return ApiClient.getEpisodes(seriesId, {
                seasonId: seasonId,
                userId: ApiClient.getCurrentUserId(),
                Fields: 'Id,Name,IndexNumber,ParentIndexNumber'
            });
        }

        getPartyList(userId = null) {
            let url = '/WatchParty/List';
            if (userId) {
                url += `?UserId=${userId}`;
            }
            return ApiClient.getJSON(ApiClient.getUrl(url));
        }

        getPartyParticipants(partyId) {
            return ApiClient.getJSON(ApiClient.getUrl(`/WatchParty/${partyId}/Participants`));
        }

        setUserReady(partyId, userId, isReady) {
            return ApiClient.ajax({
                url: ApiClient.getUrl(`/WatchParty/${partyId}/Ready`),
                type: 'POST',
                data: JSON.stringify({ UserId: userId, IsReady: isReady }),
                contentType: 'application/json'
            });
        }

        startParty(partyId, userId) {
            return ApiClient.ajax({
                url: ApiClient.getUrl(`/WatchParty/${partyId}/Start`),
                type: 'POST',
                data: JSON.stringify({ UserId: userId }),
                contentType: 'application/json'
            });
        }

        getAllUsers() {
            return ApiClient.getJSON(ApiClient.getUrl('/WatchParty/Users'));
        }
    }

    return new WatchPartyApi();
});
