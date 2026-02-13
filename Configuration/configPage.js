define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d";

    function getPluginConfiguration() {
        return ApiClient.getPluginConfiguration(pluginId);
    }

    function updatePluginConfiguration(config) {
        return ApiClient.updatePluginConfiguration(pluginId, config);
    }

    function loadLibraries() {
        return ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'));
    }

    function populateLibraryDropdown(view, select) {
        loadLibraries().then(result => {
            const libraries = result.Items || [];
            select.innerHTML = '<option value="">-- Select library --</option>';
            
            libraries.forEach(library => {
                const option = document.createElement('option');
                option.value = library.Id;
                option.dataset.name = library.Name;
                option.dataset.path = library.Locations && library.Locations.length > 0 ? library.Locations[0] : '';
                option.textContent = library.Name;
                select.appendChild(option);
            });
        }).catch(() => {
            select.innerHTML = '<option value="">Error loading libraries</option>';
        });
    }

    function loadLibraryContent(libraryId, startIndex = 0, limit = 100) {
        return ApiClient.getItems(ApiClient.getCurrentUserId(), {
            ParentId: libraryId,
            Recursive: true,
            IncludeItemTypes: 'Movie,Series',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Fields: 'Id,Name,ProductionYear,Type',
            StartIndex: startIndex,
            Limit: limit
        });
    }

    function loadSeasons(seriesId, startIndex = 0, limit = 100) {
        return ApiClient.getSeasons(seriesId, {
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber',
            StartIndex: startIndex,
            Limit: limit
        });
    }

    function loadEpisodes(seriesId, seasonId, startIndex = 0, limit = 100) {
        return ApiClient.getEpisodes(seriesId, {
            seasonId: seasonId,
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber,ParentIndexNumber',
            StartIndex: startIndex,
            Limit: limit
        });
    }

    function loadUsers() {
        return ApiClient.getUsers();
    }

    function populateUsersDropdown(view, select, selectedUserIds = []) {
        loadUsers().then(users => {
            select.innerHTML = '';
            
            users.forEach(user => {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                option.selected = selectedUserIds.includes(user.Id);
                select.appendChild(option);
            });
        }).catch(error => {
            console.error('Error loading users:', error);
            select.innerHTML = '<option value="">Error loading users</option>';
        });
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);

            view.querySelector('#watchPartyConfigForm').addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveData(view);
                return false;
            });

            view.querySelector('#btnSaveSettings').addEventListener('click', () => {
                this.saveGlobalSettings(view);
            });

            view.querySelector('#btnOpenDashboard').addEventListener('click', () => {
                const port = view.querySelector('#externalWebServerPort').value || 8097;
                window.open(`http://localhost:${port}`, '_blank');
            });

            view.querySelector('#selectedLibraryId').addEventListener('change', (e) => {
                this.onLibraryChange(view, e.target.value);
            });

            view.querySelector('#selectedItemId').addEventListener('change', (e) => {
                if (e.target.value === '__LOAD_MORE__') {
                    this.loadMoreLibraryContent(view);
                    e.target.value = '';
                } else {
                    this.onItemChange(view, e.target.value);
                }
            });

            view.querySelector('#selectedSeasonId').addEventListener('change', (e) => {
                if (e.target.value === '__LOAD_MORE__') {
                    this.loadMoreSeasons(view);
                    e.target.value = '';
                } else {
                    this.onSeasonChange(view, e.target.value);
                }
            });

            view.querySelector('#selectedEpisodeId').addEventListener('change', (e) => {
                if (e.target.value === '__LOAD_MORE__') {
                    this.loadMoreEpisodes(view);
                    e.target.value = '';
                }
            });

            view.querySelector('#autoStartWhenReady').addEventListener('change', (e) => {
                view.querySelector('#minReadyCount').disabled = !e.target.checked;
            });

            view.querySelector('#autoKickInactive').addEventListener('change', (e) => {
                view.querySelector('#inactiveTimeoutMinutes').disabled = !e.target.checked;
            });

            view.querySelector('#enableNetworkLatencyCompensation').addEventListener('change', (e) => {
                const enabled = e.target.checked;
                view.querySelector('#networkLatencyMeasurementIntervalSeconds').disabled = !enabled;
                view.querySelector('#autoAdjustForLatency').disabled = !enabled;
                view.querySelector('#maxLatencyCompensationMs').disabled = !enabled;
            });

            view.querySelector('#isWaitingRoom').addEventListener('change', (e) => {
                const autoStartCheckbox = view.querySelector('#autoStartWhenReady');
                const minReadyInput = view.querySelector('#minReadyCount');
                autoStartCheckbox.disabled = !e.target.checked;
                if (!e.target.checked) {
                    autoStartCheckbox.checked = false;
                    minReadyInput.disabled = true;
                } else {
                    minReadyInput.disabled = !autoStartCheckbox.checked;
                }
            });

            view.querySelector('#externalWebServerPort').addEventListener('input', (e) => {
                const port = e.target.value || '8097';
                const placeholders = view.querySelectorAll('#portPlaceholder, #portPlaceholderDocker, #portPlaceholderDocker2');
                placeholders.forEach(el => el.textContent = port);
            });

            view.querySelector('#searchContent').addEventListener('input', (e) => {
                this.filterDropdown(view.querySelector('#selectedItemId'), e.target.value);
            });

            view.querySelector('#searchSeason').addEventListener('input', (e) => {
                this.filterDropdown(view.querySelector('#selectedSeasonId'), e.target.value);
            });

            view.querySelector('#searchEpisode').addEventListener('input', (e) => {
                this.filterDropdown(view.querySelector('#selectedEpisodeId'), e.target.value);
            });
        }

        onLibraryChange(view, libraryId) {
            if (!libraryId) {
                view.querySelector('#selectedItemId').innerHTML = '<option value="">Select a library first...</option>';
                this.hideSeriesControls(view);
                return;
            }

            this.currentLibraryId = libraryId;
            this.libraryContentOffset = 0;
            
            const searchInput = view.querySelector('#searchContent');
            if (searchInput) searchInput.value = '';
            
            loading.show();
            loadLibraryContent(libraryId, 0, 100).then(result => {
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;
                const select = view.querySelector('#selectedItemId');
                select.innerHTML = '<option value="">-- Select content --</option>';
                
                items.forEach(item => {
                    const option = document.createElement('option');
                    option.value = item.Id;
                    option.dataset.type = item.Type;
                    const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                    const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                    option.textContent = `${item.Name}${year}${type}`;
                    select.appendChild(option);
                });

                this.libraryContentOffset = items.length;
                
                if (this.libraryContentOffset < totalCount) {
                    const loadMoreOption = document.createElement('option');
                    loadMoreOption.value = '__LOAD_MORE__';
                    loadMoreOption.textContent = `--- Load More (${this.libraryContentOffset} of ${totalCount}) ---`;
                    loadMoreOption.className = 'load-more-option';
                    select.appendChild(loadMoreOption);
                }

                loading.hide();
                this.hideSeriesControls(view);
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading content.' });
            });
        }

        onItemChange(view, itemId) {
            const select = view.querySelector('#selectedItemId');
            const selectedOption = select.selectedOptions[0];
            
            if (!itemId || !selectedOption) {
                this.hideSeriesControls(view);
                return;
            }

            const itemType = selectedOption.dataset.type;
            
            if (itemType === 'Series') {
                this.currentSeriesId = itemId;
                this.seasonsOffset = 0;
                
                const searchSeason = view.querySelector('#searchSeason');
                const searchEpisode = view.querySelector('#searchEpisode');
                if (searchSeason) searchSeason.value = '';
                if (searchEpisode) searchEpisode.value = '';
                
                this.showSeriesControls(view);
                loading.show();
                loadSeasons(itemId, 0, 100).then(result => {
                    const seasons = result.Items || [];
                    const totalCount = result.TotalRecordCount || seasons.length;
                    const seasonSelect = view.querySelector('#selectedSeasonId');
                    seasonSelect.innerHTML = '<option value="">-- Select season --</option>';
                    
                    seasons.forEach(season => {
                        const option = document.createElement('option');
                        option.value = season.Id;
                        const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                        option.textContent = `${season.Name || 'Season' + seasonNum}`;
                        seasonSelect.appendChild(option);
                    });

                    this.seasonsOffset = seasons.length;
                    
                    if (this.seasonsOffset < totalCount) {
                        const loadMoreOption = document.createElement('option');
                        loadMoreOption.value = '__LOAD_MORE__';
                        loadMoreOption.textContent = `--- Load More (${this.seasonsOffset} of ${totalCount}) ---`;
                        loadMoreOption.className = 'load-more-option';
                        seasonSelect.appendChild(loadMoreOption);
                    }

                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error loading seasons.' });
                });
            } else {
                this.hideSeriesControls(view);
            }
        }

        onSeasonChange(view, seasonId) {
            if (!seasonId) {
                const episodeSelect = view.querySelector('#selectedEpisodeId');
                episodeSelect.innerHTML = '<option value="">Select a season first...</option>';
                return;
            }

            const seriesId = view.querySelector('#selectedItemId').value;
            this.currentSeasonId = seasonId;
            this.episodesOffset = 0;
            
            const searchEpisode = view.querySelector('#searchEpisode');
            if (searchEpisode) searchEpisode.value = '';
            
            loading.show();
            loadEpisodes(seriesId, seasonId, 0, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;
                const episodeSelect = view.querySelector('#selectedEpisodeId');
                episodeSelect.innerHTML = '<option value="">-- Select episode --</option>';
                
                episodes.forEach(episode => {
                    const option = document.createElement('option');
                    option.value = episode.Id;
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    option.textContent = `${seasonNum}${epNum} - ${episode.Name}`;
                    episodeSelect.appendChild(option);
                });

                this.episodesOffset = episodes.length;
                
                if (this.episodesOffset < totalCount) {
                    const loadMoreOption = document.createElement('option');
                    loadMoreOption.value = '__LOAD_MORE__';
                    loadMoreOption.textContent = `--- Load More (${this.episodesOffset} of ${totalCount}) ---`;
                    loadMoreOption.className = 'load-more-option';
                    episodeSelect.appendChild(loadMoreOption);
                }

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading episodes.' });
            });
        }

        loadMoreLibraryContent(view) {
            if (!this.currentLibraryId) return;
            
            const select = view.querySelector('#selectedItemId');
            const loadMoreOption = select.querySelector('.load-more-option');
            if (loadMoreOption) {
                loadMoreOption.remove();
            }
            
            loading.show();
            loadLibraryContent(this.currentLibraryId, this.libraryContentOffset, 100).then(result => {
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;
                
                items.forEach(item => {
                    const option = document.createElement('option');
                    option.value = item.Id;
                    option.dataset.type = item.Type;
                    const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                    const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                    option.textContent = `${item.Name}${year}${type}`;
                    select.appendChild(option);
                });

                this.libraryContentOffset += items.length;
                
                if (this.libraryContentOffset < totalCount) {
                    const newLoadMoreOption = document.createElement('option');
                    newLoadMoreOption.value = '__LOAD_MORE__';
                    newLoadMoreOption.textContent = `--- Load More (${this.libraryContentOffset} of ${totalCount}) ---`;
                    newLoadMoreOption.className = 'load-more-option';
                    select.appendChild(newLoadMoreOption);
                }

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more content.' });
            });
        }

        loadMoreSeasons(view) {
            if (!this.currentSeriesId) return;
            
            const select = view.querySelector('#selectedSeasonId');
            const loadMoreOption = select.querySelector('.load-more-option');
            if (loadMoreOption) {
                loadMoreOption.remove();
            }
            
            loading.show();
            loadSeasons(this.currentSeriesId, this.seasonsOffset, 100).then(result => {
                const seasons = result.Items || [];
                const totalCount = result.TotalRecordCount || seasons.length;
                
                seasons.forEach(season => {
                    const option = document.createElement('option');
                    option.value = season.Id;
                    const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                    option.textContent = `${season.Name || 'Season' + seasonNum}`;
                    select.appendChild(option);
                });

                this.seasonsOffset += seasons.length;
                
                if (this.seasonsOffset < totalCount) {
                    const newLoadMoreOption = document.createElement('option');
                    newLoadMoreOption.value = '__LOAD_MORE__';
                    newLoadMoreOption.textContent = `--- Load More (${this.seasonsOffset} of ${totalCount}) ---`;
                    newLoadMoreOption.className = 'load-more-option';
                    select.appendChild(newLoadMoreOption);
                }

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more seasons.' });
            });
        }

        loadMoreEpisodes(view) {
            if (!this.currentSeasonId) return;
            
            const seriesId = view.querySelector('#selectedItemId').value;
            const select = view.querySelector('#selectedEpisodeId');
            const loadMoreOption = select.querySelector('.load-more-option');
            if (loadMoreOption) {
                loadMoreOption.remove();
            }
            
            loading.show();
            loadEpisodes(seriesId, this.currentSeasonId, this.episodesOffset, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;
                
                episodes.forEach(episode => {
                    const option = document.createElement('option');
                    option.value = episode.Id;
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    option.textContent = `${seasonNum}${epNum} - ${episode.Name}`;
                    select.appendChild(option);
                });

                this.episodesOffset += episodes.length;
                
                if (this.episodesOffset < totalCount) {
                    const newLoadMoreOption = document.createElement('option');
                    newLoadMoreOption.value = '__LOAD_MORE__';
                    newLoadMoreOption.textContent = `--- Load More (${this.episodesOffset} of ${totalCount}) ---`;
                    newLoadMoreOption.className = 'load-more-option';
                    select.appendChild(newLoadMoreOption);
                }

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more episodes.' });
            });
        }

        filterDropdown(select, searchQuery) {
            const query = searchQuery.toLowerCase().trim();
            const options = Array.from(select.options);
            
            options.forEach(option => {
                if (option.value === '' || option.value === '__LOAD_MORE__') {
                    option.style.display = '';
                    return;
                }
                
                const text = option.textContent.toLowerCase();
                if (query === '' || text.includes(query)) {
                    option.style.display = '';
                } else {
                    option.style.display = 'none';
                }
            });
        }

        showSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'block';
            view.querySelector('#episodeContainer').style.display = 'block';
        }

        hideSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'none';
            view.querySelector('#episodeContainer').style.display = 'none';
            view.querySelector('#selectedSeasonId').innerHTML = '<option value="">Select a TV show first...</option>';
            view.querySelector('#selectedEpisodeId').innerHTML = '<option value="">Select a season first...</option>';
            
            const searchSeason = view.querySelector('#searchSeason');
            const searchEpisode = view.querySelector('#searchEpisode');
            if (searchSeason) searchSeason.value = '';
            if (searchEpisode) searchEpisode.value = '';
        }

        renderPartyList(view, config) {
            const container = view.querySelector('#activePartiesList');
            
            if (!config.WatchParties || config.WatchParties.length === 0) {
                container.innerHTML = '<p style="color: #999;">No active watch parties</p>';
                return;
            }

            let html = '<div style="display: flex; flex-direction: column; gap: 1em;">';
            
            config.WatchParties.forEach(party => {
                const statusColor = party.IsActive ? '#4CAF50' : '#999';
                const statusText = party.IsActive ? 'Active' : 'Inactive';
                const created = new Date(party.CreatedDate).toLocaleDateString();
                
                const features = [];
                if (party.IsWaitingRoom) features.push('Waiting Room');
                if (party.PauseControl && party.PauseControl !== 'Anyone') features.push(`Pause: ${party.PauseControl}`);
                if (party.HostOnlySeek) features.push('Host-Only Seek');
                if (party.LockSeekAhead) features.push('Lock Seek Ahead');
                if (party.AutoKickInactive) features.push('Auto-Kick Inactive');
                const featuresText = features.length > 0 ? `<br>Features: ${features.join(', ')}` : '';
                
                html += `
                    <div class="paper-card" style="padding: 1em; display: flex; justify-content: space-between; align-items: center;">
                        <div style="flex: 1;">
                            <div style="font-weight: 500; margin-bottom: 0.5em;">
                                ${party.ItemName || 'Unnamed Party'}
                                <span style="color: ${statusColor}; font-size: 0.9em; margin-left: 0.5em;">● ${statusText}</span>
                            </div>
                            <div style="font-size: 0.85em; color: #999;">
                                Library: ${party.CollectionName || 'Watch Party'} | 
                                Type: ${party.ItemType || 'Unknown'} | 
                                Max: ${party.MaxParticipants || 50} viewers | 
                                Created: ${created}${featuresText}
                            </div>
                        </div>
                        <div style="display: flex; gap: 0.5em;">
                            <button is="emby-button" class="button-flat btnToggleParty" data-partyid="${party.Id}" style="padding: 0.5em 1em;">
                                <span>${party.IsActive ? 'Deactivate' : 'Activate'}</span>
                            </button>
                            <button is="emby-button" class="button-flat btnDeleteParty" data-partyid="${party.Id}" style="padding: 0.5em 1em; color: #f44336;">
                                <span>Delete</span>
                            </button>
                        </div>
                    </div>
                `;
            });
            
            html += '</div>';
            container.innerHTML = html;
            
            container.querySelectorAll('.btnDeleteParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = e.target.closest('button').dataset.partyid;
                    this.deleteParty(view, partyId);
                });
            });
            
            container.querySelectorAll('.btnToggleParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = e.target.closest('button').dataset.partyid;
                    this.toggleParty(view, partyId);
                });
            });
        }

        deleteParty(view, partyId) {
            if (!confirm('Are you sure you want to delete this watch party?')) {
                return;
            }

            loading.show();
            getPluginConfiguration().then(config => {
                config.WatchParties = config.WatchParties.filter(p => p.Id !== partyId);
                
                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                    this.renderPartyList(view, config);
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error deleting party.' });
                });            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading configuration.' });            });
        }

        toggleParty(view, partyId) {
            loading.show();
            getPluginConfiguration().then(config => {
                const party = config.WatchParties.find(p => p.Id === partyId);
                if (party) {
                    party.IsActive = !party.IsActive;
                    
                    updatePluginConfiguration(config).then(result => {
                        loading.hide();
                        Dashboard.processPluginConfigurationUpdateResult(result);
                        this.renderPartyList(view, config);
                    }).catch(() => {
                        loading.hide();
                        toast({ type: 'error', text: 'Error updating party.' });
                    });
                }
            });
        }

        loadData(view) {
            loading.show();
            
            Promise.all([getPluginConfiguration(), loadLibraries()]).then(([config, librariesResult]) => {
                this.config = config;
                const libraries = librariesResult.Items || [];
                
                const librarySelect = view.querySelector('#selectedLibraryId');
                librarySelect.innerHTML = '<option value="">-- Select a library --</option>';
                
                libraries.forEach(library => {
                    const option = document.createElement('option');
                    option.value = library.Id;
                    option.textContent = library.Name;
                    librarySelect.appendChild(option);
                });

                view.querySelector('#isPartyActive').checked = false;
                view.querySelector('#maxParticipants').value = 50;
                view.querySelector('#syncIntervalSeconds').value = config.SyncIntervalSeconds || 5;
                view.querySelector('#syncOffsetMilliseconds').value = config.SyncOffsetMilliseconds || 1000;
                view.querySelector('#enableDebugLogging').checked = config.EnableDebugLogging || false;
                view.querySelector('#enableExternalWebServer').checked = config.EnableExternalWebServer !== false;
                view.querySelector('#externalWebServerPort').value = config.ExternalWebServerPort || 8097;
                view.querySelector('#externalServerUrl').value = config.ExternalServerUrl || '';
                view.querySelector('#embyApiKey').value = config.EmbyApiKey || '';
                
                const strmLibrarySelect = view.querySelector('#strmTargetLibrary');
                populateLibraryDropdown(view, strmLibrarySelect);
                if (config.StrmTargetLibraryId) {
                    setTimeout(() => {
                        strmLibrarySelect.value = config.StrmTargetLibraryId;
                    }, 100);
                }
                
                const port = config.ExternalWebServerPort || 8097;
                const placeholders = view.querySelectorAll('#portPlaceholder, #portPlaceholderDocker, #portPlaceholderDocker2');
                placeholders.forEach(el => el.textContent = port);
                
                const adminPasswordField = view.querySelector('#adminPassword');
                if (config.AdminPasswordHash) {
                    adminPasswordField.placeholder = 'Password is set - enter new password to change';
                } else {
                    adminPasswordField.placeholder = 'No password set';
                }
                
                this.checkWebServerStatus(view, config);

                view.querySelector('#isWaitingRoom').checked = true;
                view.querySelector('#autoStartWhenReady').checked = true;
                view.querySelector('#autoStartWhenReady').disabled = false;
                view.querySelector('#minReadyCount').value = 1;
                view.querySelector('#minReadyCount').disabled = false;
                view.querySelector('#pauseControl').value = 'Anyone';
                view.querySelector('#hostOnlySeek').checked = true;
                view.querySelector('#lockSeekAhead').checked = true;
                view.querySelector('#syncToleranceSeconds').value = 10;
                view.querySelector('#maxBufferThresholdSeconds').value = 30;
                view.querySelector('#autoKickInactive').checked = true;
                view.querySelector('#inactiveTimeoutMinutes').value = 15;
                view.querySelector('#inactiveTimeoutMinutes').disabled = false;
                view.querySelector('#enableNetworkLatencyCompensation').checked = true;
                view.querySelector('#networkLatencyMeasurementIntervalSeconds').value = 30;
                view.querySelector('#autoAdjustForLatency').checked = true;
                view.querySelector('#maxLatencyCompensationMs').value = 5000;
                view.querySelector('#networkLatencyMeasurementIntervalSeconds').disabled = false;
                view.querySelector('#autoAdjustForLatency').disabled = false;
                view.querySelector('#maxLatencyCompensationMs').disabled = false;

                const libraryNameSelect = view.querySelector('#libraryName');
                populateLibraryDropdown(view, libraryNameSelect);

                const allowedUsersSelect = view.querySelector('#allowedUsers');
                populateUsersDropdown(view, allowedUsersSelect);

                const masterUserSelect = view.querySelector('#masterUser');
                populateUsersDropdown(view, masterUserSelect);

                this.renderPartyList(view, config);

                if (config.SelectedLibraryId) {
                    view.querySelector('#selectedLibraryId').value = config.SelectedLibraryId;
                    
                    loadLibraryContent(config.SelectedLibraryId).then(result => {
                        const items = result.Items || [];
                        const select = view.querySelector('#selectedItemId');
                        select.innerHTML = '<option value="">-- Select content --</option>';
                        
                        items.forEach(item => {
                            const option = document.createElement('option');
                            option.value = item.Id;
                            option.dataset.type = item.Type;
                            const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                            const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                            option.textContent = `${item.Name}${year}${type}`;
                            select.appendChild(option);
                        });

                        if (config.SelectedItemType === 'Episode' && config.SelectedSeriesId) {
                            select.value = config.SelectedSeriesId;
                            this.showSeriesControls(view);
                            
                            loadSeasons(config.SelectedSeriesId).then(seasonsResult => {
                                const seasons = seasonsResult.Items || [];
                                const seasonSelect = view.querySelector('#selectedSeasonId');
                                seasonSelect.innerHTML = '<option value="">-- Select season --</option>';
                                
                                seasons.forEach(season => {
                                    const option = document.createElement('option');
                                    option.value = season.Id;
                                    const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                                    option.textContent = `${season.Name || 'Season' + seasonNum}`;
                                    seasonSelect.appendChild(option);
                                });

                                if (config.SelectedSeasonId) {
                                    seasonSelect.value = config.SelectedSeasonId;
                                    
                                    loadEpisodes(config.SelectedSeriesId, config.SelectedSeasonId).then(episodesResult => {
                                        const episodes = episodesResult.Items || [];
                                        const episodeSelect = view.querySelector('#selectedEpisodeId');
                                        episodeSelect.innerHTML = '<option value="">-- Select episode --</option>';
                                        
                                        episodes.forEach(episode => {
                                            const option = document.createElement('option');
                                            option.value = episode.Id;
                                            const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                                            const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                                            option.textContent = `${seasonNum}${epNum} - ${episode.Name}`;
                                            episodeSelect.appendChild(option);
                                        });

                                        if (config.SelectedItemId) {
                                            episodeSelect.value = config.SelectedItemId;
                                        }
                                    });
                                }
                            });
                        } else if (config.SelectedItemId) {
                            select.value = config.SelectedItemId;
                        }
                    });
                }

                loading.hide();
                
                // Load donate image
                const donateImg = view.querySelector('#donateImage');
                if (donateImg && !donateImg.src) {
                    fetch(ApiClient.getUrl('WatchPartyForEmby/Images/donate.png'), {
                        headers: {
                            'X-Emby-Token': ApiClient.accessToken()
                        },
                        cache: 'force-cache'
                    })
                    .then(response => response.blob())
                    .then(blob => {
                        const objectUrl = URL.createObjectURL(blob);
                        donateImg.src = objectUrl;
                    })
                    .catch(error => {
                        console.error('Failed to load donate image:', error);
                    });
                }
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading configuration.' });
            });
        }

        saveData(view) {
            loading.show();
            
            const libraryId = view.querySelector('#selectedLibraryId').value;
            const itemSelect = view.querySelector('#selectedItemId');
            const itemId = itemSelect.value;
            const selectedItemOption = itemSelect.selectedOptions[0];
            
            if (!itemId) {
                loading.hide();
                toast({ type: 'error', text: 'Please select content to watch.' });
                return;
            }

            const itemType = selectedItemOption ? selectedItemOption.dataset.type : null;
            
            let finalItemId = itemId;
            let finalItemName = selectedItemOption ? selectedItemOption.textContent : '';
            let finalItemType = itemType;
            let seriesId = null;
            let seasonId = null;

            if (itemType === 'Series') {
                const episodeSelect = view.querySelector('#selectedEpisodeId');
                const episodeId = episodeSelect.value;
                
                if (!episodeId) {
                    loading.hide();
                    toast({ type: 'error', text: 'Please select an episode for TV shows.' });
                    return;
                }

                finalItemId = episodeId;
                finalItemType = 'Episode';
                seriesId = itemId;
                seasonId = view.querySelector('#selectedSeasonId').value;
                
                const episodeOption = episodeSelect.selectedOptions[0];
                finalItemName = episodeOption ? episodeOption.textContent : '';
            }
            
            const libraryNameSelect = view.querySelector('#libraryName');
            const selectedLibraryOption = libraryNameSelect.selectedOptions[0];
            const selectedLibraryId = libraryNameSelect.value;
            const selectedLibraryName = selectedLibraryOption ? selectedLibraryOption.dataset.name : 'Watch Party';
            const selectedLibraryPath = selectedLibraryOption ? selectedLibraryOption.dataset.path : '';

            if (!selectedLibraryId) {
                loading.hide();
                toast({ type: 'error', text: 'Please select a library.' });
                return;
            }

            const maxParticipants = parseInt(view.querySelector('#maxParticipants').value);
            if (maxParticipants < 2 || maxParticipants > 100) {
                loading.hide();
                toast({ type: 'error', text: 'Maximum participants must be between 2 and 100.' });
                return;
            }

            const syncTolerance = parseInt(view.querySelector('#syncToleranceSeconds').value);
            if (syncTolerance < 1 || syncTolerance > 60) {
                loading.hide();
                toast({ type: 'error', text: 'Sync tolerance must be between 1 and 60 seconds.' });
                return;
            }

            const allowedUsersSelect = view.querySelector('#allowedUsers');
            const allowedUserIds = Array.from(allowedUsersSelect.selectedOptions).map(opt => opt.value);

            const masterUserId = view.querySelector('#masterUser').value;
            if (!masterUserId) {
                loading.hide();
                toast({ type: 'error', text: 'Please select a master user who will control playback.' });
                return;
            }
            
            const partyPassword = view.querySelector('#partyPassword').value || '';

            getPluginConfiguration().then(async config => {
                const partyPasswordHash = partyPassword ? await this.hashPassword(partyPassword) : '';
                
                const newParty = {
                    Id: this.generateGuid(),
                    LibraryId: libraryId,
                    ItemId: finalItemId,
                    ItemName: finalItemName,
                    ItemType: finalItemType,
                    SeriesId: seriesId,
                    SeasonId: seasonId,
                    CollectionName: selectedLibraryName,
                    TargetLibraryId: selectedLibraryId,
                    TargetLibraryPath: selectedLibraryPath,
                    IsActive: view.querySelector('#isPartyActive').checked,
                    CurrentPositionTicks: 0,
                    IsPlaying: false,
                    MaxParticipants: parseInt(view.querySelector('#maxParticipants').value) || 50,
                    AllowedUserIds: allowedUserIds,
                    MasterUserId: masterUserId,
                    PasswordHash: partyPasswordHash,
                    IsWaitingRoom: view.querySelector('#isWaitingRoom').checked || false,
                    AutoStartWhenReady: view.querySelector('#autoStartWhenReady').checked || false,
                    MinReadyCount: parseInt(view.querySelector('#minReadyCount').value) || 1,
                    PauseControl: view.querySelector('#pauseControl').value || 'Anyone',
                    HostOnlySeek: view.querySelector('#hostOnlySeek').checked || false,
                    LockSeekAhead: view.querySelector('#lockSeekAhead').checked || false,
                    SyncToleranceSeconds: parseInt(view.querySelector('#syncToleranceSeconds').value) || 10,
                    MaxBufferThresholdSeconds: parseInt(view.querySelector('#maxBufferThresholdSeconds').value) || 30,
                    AutoKickInactiveMinutes: view.querySelector('#autoKickInactive').checked || false,
                    InactiveTimeoutMinutes: parseInt(view.querySelector('#inactiveTimeoutMinutes').value) || 15,
                    EnableNetworkLatencyCompensation: view.querySelector('#enableNetworkLatencyCompensation').checked || false,
                    NetworkLatencyMeasurementIntervalSeconds: parseInt(view.querySelector('#networkLatencyMeasurementIntervalSeconds').value) || 30,
                    AutoAdjustForLatency: view.querySelector('#autoAdjustForLatency').checked !== false,
                    MaxLatencyCompensationMs: parseInt(view.querySelector('#maxLatencyCompensationMs').value) || 5000,
                    CreatedDate: new Date().toISOString()
                };

                if (!config.WatchParties) {
                    config.WatchParties = [];
                }
                config.WatchParties.push(newParty);
                this.config = config;

                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                    
                    view.querySelector('#selectedLibraryId').value = '';
                    view.querySelector('#selectedItemId').innerHTML = '<option value="">Select a library first...</option>';
                    view.querySelector('#isPartyActive').checked = false;
                    view.querySelector('#maxParticipants').value = 50;
                    view.querySelector('#libraryName').value = '';
                    view.querySelector('#allowedUsers').selectedIndex = -1;
                    view.querySelector('#isWaitingRoom').checked = false;
                    view.querySelector('#autoStartWhenReady').checked = false;
                    view.querySelector('#minReadyCount').value = 1;
                    view.querySelector('#pauseControl').value = 'Anyone';
                    view.querySelector('#hostOnlySeek').checked = false;
                    view.querySelector('#lockSeekAhead').checked = false;
                    view.querySelector('#syncToleranceSeconds').value = 10;
                    view.querySelector('#maxBufferThresholdSeconds').value = 30;
                    view.querySelector('#autoKickInactive').checked = false;
                    view.querySelector('#inactiveTimeoutMinutes').value = 15;
                    view.querySelector('#enableNetworkLatencyCompensation').checked = false;
                    view.querySelector('#networkLatencyMeasurementIntervalSeconds').value = 30;
                    view.querySelector('#autoAdjustForLatency').checked = true;
                    view.querySelector('#maxLatencyCompensationMs').value = 5000;
                    view.querySelector('#libraryName').value = '';
                    view.querySelector('#isPartyActive').checked = false;
                    view.querySelector('#maxParticipants').value = 50;
                    this.hideSeriesControls(view);
                    
                    this.renderPartyList(view, this.config);
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error creating watch party.' });
                });
            });
        }

        saveGlobalSettings(view) {
            loading.show();
            
            getPluginConfiguration().then(async config => {
                config.SyncIntervalSeconds = parseInt(view.querySelector('#syncIntervalSeconds').value) || 5;
                config.SyncOffsetMilliseconds = parseInt(view.querySelector('#syncOffsetMilliseconds').value) || 1000;
                config.EnableDebugLogging = view.querySelector('#enableDebugLogging').checked;
                config.EnableExternalWebServer = view.querySelector('#enableExternalWebServer').checked;
                config.ExternalWebServerPort = parseInt(view.querySelector('#externalWebServerPort').value) || 8097;
                config.ExternalServerUrl = view.querySelector('#externalServerUrl').value.trim();
                config.EmbyApiKey = view.querySelector('#embyApiKey').value.trim();
                
                const strmLibrarySelect = view.querySelector('#strmTargetLibrary');
                const strmLibraryOption = strmLibrarySelect.selectedOptions[0];
                if (strmLibraryOption && strmLibraryOption.value) {
                    config.StrmTargetLibraryId = strmLibraryOption.value;
                    config.StrmTargetLibraryName = strmLibraryOption.dataset.name || strmLibraryOption.textContent;
                }
                
                const adminPassword = view.querySelector('#adminPassword').value.trim();
                if (adminPassword) {
                    config.AdminPasswordHash = await this.hashPassword(adminPassword);
                    view.querySelector('#adminPassword').value = '';
                    view.querySelector('#adminPassword').placeholder = 'Password is set - enter new password to change';
                }

                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                    
                    this.config = config;
                    setTimeout(() => {
                        this.checkWebServerStatus(view, config);
                    }, 2000);
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error saving settings.' });
                });
            });
        }

        checkWebServerStatus(view, config) {
            const statusElement = view.querySelector('#webServerStatusText');
            const port = config.ExternalWebServerPort || 8097;
            const enabled = config.EnableExternalWebServer !== false;
            
            if (!enabled) {
                statusElement.textContent = 'Disabled';
                statusElement.style.color = '#999';
                return;
            }
            
            statusElement.textContent = 'Checking...';
            statusElement.style.color = '#FFA500';
            
            fetch(`http://localhost:${port}/`)
                .then(response => {
                    if (response.ok) {
                        statusElement.innerHTML = `✓ Running on port ${port}<br><a href="http://localhost:${port}/" target="_blank" style="color: #4CAF50;">Open Dashboard</a>`;
                        statusElement.style.color = '#4CAF50';
                    } else {
                        statusElement.textContent = `Error: HTTP ${response.status}`;
                        statusElement.style.color = '#F44336';
                    }
                })
                .catch(error => {
                    statusElement.innerHTML = `✗ Not Running<br><small style="color: #999;">May need: <code style="background: #222; padding: 0.2em 0.4em; border-radius: 3px;">netsh http add urlacl url=http://*:${port}/ user="Everyone"</code></small>`;
                    statusElement.style.color = '#F44336';
                });
        }

        async hashPassword(password) {
            const msgBuffer = new TextEncoder().encode(password);
            const hashBuffer = await crypto.subtle.digest('SHA-256', msgBuffer);
            const hashArray = Array.from(new Uint8Array(hashBuffer));
            const hashBase64 = btoa(String.fromCharCode.apply(null, hashArray));
            return hashBase64;
        }

        generateGuid() {
            return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                const r = Math.random() * 16 | 0;
                const v = c === 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            });
        }

        onResume(options) {
            super.onResume(options);
            this.loadData(this.view);
            
            const libraryNameSelect = this.view.querySelector('#libraryName');
            if (libraryNameSelect) {
                populateLibraryDropdown(this.view, libraryNameSelect);
            }
        }
    }
});
