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

    function loadLibraryContent(libraryId, startIndex = 0, limit = 100, searchTerm = '') {
        const params = {
            ParentId: libraryId,
            Recursive: true,
            IncludeItemTypes: 'Movie,Series',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Fields: 'Id,Name,ProductionYear,Type',
            StartIndex: startIndex,
            Limit: limit
        };
        
        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }
        
        return ApiClient.getItems(ApiClient.getCurrentUserId(), params);
    }

    function loadSeasons(seriesId, startIndex = 0, limit = 100, searchTerm = '') {
        const params = {
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber',
            StartIndex: startIndex,
            Limit: limit
        };
        
        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }
        
        return ApiClient.getSeasons(seriesId, params);
    }

    function loadEpisodes(seriesId, seasonId, startIndex = 0, limit = 100, searchTerm = '') {
        const params = {
            seasonId: seasonId,
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber,ParentIndexNumber',
            StartIndex: startIndex,
            Limit: limit
        };
        
        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }
        
        return ApiClient.getEpisodes(seriesId, params);
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

            this.setupAutocomplete(view, 'searchContent', 'selectedItemId', 'searchContentDropdown', 
                (itemData) => this.onItemSelect(view, itemData),
                () => this.loadMoreLibraryContent(view)
            );

            this.setupAutocomplete(view, 'searchSeason', 'selectedSeasonId', 'searchSeasonDropdown',
                (itemData) => this.onSeasonSelect(view, itemData),
                () => this.loadMoreSeasons(view)
            );

            this.setupAutocomplete(view, 'searchEpisode', 'selectedEpisodeId', 'searchEpisodeDropdown',
                (itemData) => this.onEpisodeSelect(view, itemData),
                () => this.loadMoreEpisodes(view)
            );

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

            view.querySelector('#useReverseProxy').addEventListener('change', (e) => {
                this.toggleReverseProxySettings(view, e.target.checked);
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
        }

        setupAutocomplete(view, searchInputId, hiddenInputId, dropdownId, onSelectCallback, onLoadMoreCallback) {
            const searchInput = view.querySelector(`#${searchInputId}`);
            const hiddenInput = view.querySelector(`#${hiddenInputId}`);
            const dropdown = view.querySelector(`#${dropdownId}`);
            
            let searchTimeout;
            
            searchInput.addEventListener('focus', () => {
                if (this[`${searchInputId}_items`] && this[`${searchInputId}_items`].length > 0) {
                    this.renderDropdown(view, searchInputId, hiddenInputId, dropdownId, '', onSelectCallback, onLoadMoreCallback);
                    dropdown.classList.add('show');
                }
            });
            
            searchInput.addEventListener('input', (e) => {
                const query = e.target.value.trim();
                hiddenInput.value = '';
                
                clearTimeout(searchTimeout);
                
                if (query.length >= 2) {
                    searchTimeout = setTimeout(() => {
                        this.performSearch(view, searchInputId, query, onSelectCallback, onLoadMoreCallback);
                    }, 300);
                } else if (query.length === 0) {
                    if (this[`${searchInputId}_items`] && this[`${searchInputId}_items`].length > 0) {
                        this.renderDropdown(view, searchInputId, hiddenInputId, dropdownId, query, onSelectCallback, onLoadMoreCallback);
                        dropdown.classList.add('show');
                    } else {
                        dropdown.classList.remove('show');
                    }
                }
            });
            
            document.addEventListener('click', (e) => {
                if (!searchInput.contains(e.target) && !dropdown.contains(e.target)) {
                    dropdown.classList.remove('show');
                }
            });
        }

        renderDropdown(view, searchInputId, hiddenInputId, dropdownId, query, onSelectCallback, onLoadMoreCallback) {
            const searchInput = view.querySelector(`#${searchInputId}`);
            const hiddenInput = view.querySelector(`#${hiddenInputId}`);
            const dropdown = view.querySelector(`#${dropdownId}`);
            const items = this[`${searchInputId}_items`] || [];
            const metadata = this[`${searchInputId}_metadata`] || {};
            
            const lowerQuery = query.toLowerCase().trim();
            const filteredItems = items.filter(item => {
                if (!lowerQuery) return true;
                return item.text.toLowerCase().includes(lowerQuery);
            });
            
            dropdown.innerHTML = '';
            
            if (filteredItems.length === 0 && !metadata.hasMore) {
                const noResults = document.createElement('div');
                noResults.className = 'autocomplete-item';
                noResults.textContent = 'No results found';
                noResults.style.textAlign = 'center';
                noResults.style.color = '#999';
                dropdown.appendChild(noResults);
                return;
            }
            
            filteredItems.forEach(item => {
                const div = document.createElement('div');
                div.className = 'autocomplete-item';
                div.textContent = item.text;
                div.dataset.value = item.id;
                
                div.addEventListener('click', () => {
                    searchInput.value = item.text;
                    hiddenInput.value = item.id;
                    hiddenInput.dataset.type = item.type || '';
                    hiddenInput.dataset.name = item.name || item.text;
                    dropdown.classList.remove('show');
                    
                    if (onSelectCallback) {
                        onSelectCallback({
                            id: item.id,
                            text: item.text,
                            type: item.type,
                            name: item.name || item.text
                        });
                    }
                });
                
                dropdown.appendChild(div);
            });
            
            if (metadata.hasMore && !lowerQuery) {
                const loadMore = document.createElement('div');
                loadMore.className = 'autocomplete-item load-more';
                loadMore.textContent = `--- Load More (${metadata.currentCount} of ${metadata.totalCount}) ---`;
                loadMore.addEventListener('click', () => {
                    if (onLoadMoreCallback) {
                        onLoadMoreCallback();
                    }
                });
                dropdown.appendChild(loadMore);
            }
        }
        
        performSearch(view, searchInputId, query, onSelectCallback, onLoadMoreCallback) {
            const dropdown = view.querySelector(`#${searchInputId}Dropdown`);
            
            if (searchInputId === 'searchContent' && this.currentLibraryId) {
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadLibraryContent(this.currentLibraryId, 0, 50, query).then(result => {
                    const items = result.Items || [];
                    const totalCount = result.TotalRecordCount || items.length;
                    
                    this[`${searchInputId}_items`] = items.map(item => {
                        const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                        const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                        return {
                            id: item.Id,
                            text: `${item.Name}${year}${type}`,
                            type: item.Type,
                            name: item.Name
                        };
                    });
                    
                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: items.length,
                        totalCount: totalCount
                    };
                    
                    this.renderDropdown(view, searchInputId, `${searchInputId === 'searchContent' ? 'selectedItemId' : searchInputId === 'searchSeason' ? 'selectedSeasonId' : 'selectedEpisodeId'}`, `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    dropdown.classList.add('show');
                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error searching content.' });
                });
            } else if (searchInputId === 'searchSeason' && this.currentSeriesId) {
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadSeasons(this.currentSeriesId, 0, 50, query).then(result => {
                    const seasons = result.Items || [];
                    const totalCount = result.TotalRecordCount || seasons.length;
                    
                    this[`${searchInputId}_items`] = seasons.map(season => {
                        const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                        return {
                            id: season.Id,
                            text: `${season.Name || 'Season' + seasonNum}`,
                            name: season.Name || 'Season' + seasonNum
                        };
                    });
                    
                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: seasons.length,
                        totalCount: totalCount
                    };
                    
                    this.renderDropdown(view, searchInputId, 'selectedSeasonId', `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    dropdown.classList.add('show');
                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error searching seasons.' });
                });
            } else if (searchInputId === 'searchEpisode' && this.currentSeasonId) {
                const seriesId = view.querySelector('#selectedItemId').value;
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadEpisodes(seriesId, this.currentSeasonId, 0, 50, query).then(result => {
                    const episodes = result.Items || [];
                    const totalCount = result.TotalRecordCount || episodes.length;
                    
                    this[`${searchInputId}_items`] = episodes.map(episode => {
                        const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                        const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                        return {
                            id: episode.Id,
                            text: `${seasonNum}${epNum} - ${episode.Name}`,
                            name: episode.Name
                        };
                    });
                    
                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: episodes.length,
                        totalCount: totalCount
                    };
                    
                    this.renderDropdown(view, searchInputId, 'selectedEpisodeId', `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    dropdown.classList.add('show');
                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error searching episodes.' });
                });
            }
        }
        
        onLibraryChange(view, libraryId) {
            if (!libraryId) {
                this.searchContent_items = [];
                this.searchContent_searchMode = false;
                view.querySelector('#searchContent').value = '';
                view.querySelector('#selectedItemId').value = '';
                this.hideSeriesControls(view);
                return;
            }

            this.currentLibraryId = libraryId;
            this.libraryContentOffset = 0;
            this.searchContent_searchMode = false;
            
            const searchInput = view.querySelector('#searchContent');
            if (searchInput) searchInput.value = '';
            view.querySelector('#selectedItemId').value = '';
            
            loading.show();
            loadLibraryContent(libraryId, 0, 100).then(result => {
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;
                
                this.searchContent_items = items.map(item => {
                    const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                    const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                    return {
                        id: item.Id,
                        text: `${item.Name}${year}${type}`,
                        type: item.Type,
                        name: item.Name
                    };
                });
                
                this.searchContent_metadata = {
                    hasMore: items.length < totalCount,
                    currentCount: items.length,
                    totalCount: totalCount
                };

                this.libraryContentOffset = items.length;

                loading.hide();
                this.hideSeriesControls(view);
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading content.' });
            });
        }

        onItemSelect(view, itemData) {
            if (!itemData.id) {
                this.hideSeriesControls(view);
                return;
            }

            if (itemData.type === 'Series') {
                this.currentSeriesId = itemData.id;
                this.seasonsOffset = 0;
                this.searchSeason_searchMode = false;
                
                const searchSeason = view.querySelector('#searchSeason');
                const searchEpisode = view.querySelector('#searchEpisode');
                if (searchSeason) searchSeason.value = '';
                if (searchEpisode) searchEpisode.value = '';
                view.querySelector('#selectedSeasonId').value = '';
                view.querySelector('#selectedEpisodeId').value = '';
                
                this.showSeriesControls(view);
                loading.show();
                loadSeasons(itemData.id, 0, 100).then(result => {
                    const seasons = result.Items || [];
                    const totalCount = result.TotalRecordCount || seasons.length;
                    
                    this.searchSeason_items = seasons.map(season => {
                        const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                        return {
                            id: season.Id,
                            text: `${season.Name || 'Season' + seasonNum}`,
                            name: season.Name || 'Season' + seasonNum
                        };
                    });
                    
                    this.searchSeason_metadata = {
                        hasMore: seasons.length < totalCount,
                        currentCount: seasons.length,
                        totalCount: totalCount
                    };

                    this.seasonsOffset = seasons.length;

                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: 'Error loading seasons.' });
                });
            } else {
                this.hideSeriesControls(view);
            }
        }

        onSeasonSelect(view, itemData) {
            if (!itemData.id) {
                this.searchEpisode_searchMode = false;
                view.querySelector('#searchEpisode').value = '';
                view.querySelector('#selectedEpisodeId').value = '';
                return;
            }

            const seriesId = view.querySelector('#selectedItemId').value;
            this.currentSeasonId = itemData.id;
            this.episodesOffset = 0;
            this.searchEpisode_searchMode = falseitemData.id;
            this.episodesOffset = 0;
            
            const searchEpisode = view.querySelector('#searchEpisode');
            if (searchEpisode) searchEpisode.value = '';
            view.querySelector('#selectedEpisodeId').value = '';
            
            loading.show();
            loadEpisodes(seriesId, itemData.id, 0, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;
                
                this.searchEpisode_items = episodes.map(episode => {
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    return {
                        id: episode.Id,
                        text: `${seasonNum}${epNum} - ${episode.Name}`,
                        name: episode.Name
                    };
                });
                
                this.searchEpisode_metadata = {
                    hasMore: episodes.length < totalCount,
                    currentCount: episodes.length,
                    totalCount: totalCount
                };

                this.episodesOffset = episodes.length;

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading episodes.' });
            });
        }

        onEpisodeSelect(view, itemData) {
        }

        loadMoreLibraryContent(view) {
            if (!this.currentLibraryId) return;
            
            loading.show();
            loadLibraryContent(this.currentLibraryId, this.libraryContentOffset, 100).then(result => {
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;
                
                const newItems = items.map(item => {
                    const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                    const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                    return {
                        id: item.Id,
                        text: `${item.Name}${year}${type}`,
                        type: item.Type,
                        name: item.Name
                    };
                });
                
                this.searchContent_items = this.searchContent_items.concat(newItems);
                this.libraryContentOffset += items.length;
                
                this.searchContent_metadata = {
                    hasMore: this.libraryContentOffset < totalCount,
                    currentCount: this.libraryContentOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchContent', 'selectedItemId', 'searchContentDropdown',
                    view.querySelector('#searchContent').value,
                    (itemData) => this.onItemSelect(view, itemData),
                    () => this.loadMoreLibraryContent(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more content.' });
            });
        }

        loadMoreSeasons(view) {
            if (!this.currentSeriesId) return;
            
            loading.show();
            loadSeasons(this.currentSeriesId, this.seasonsOffset, 100).then(result => {
                const seasons = result.Items || [];
                const totalCount = result.TotalRecordCount || seasons.length;
                
                const newItems = seasons.map(season => {
                    const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                    return {
                        id: season.Id,
                        text: `${season.Name || 'Season' + seasonNum}`,
                        name: season.Name || 'Season' + seasonNum
                    };
                });
                
                this.searchSeason_items = this.searchSeason_items.concat(newItems);
                this.seasonsOffset += seasons.length;
                
                this.searchSeason_metadata = {
                    hasMore: this.seasonsOffset < totalCount,
                    currentCount: this.seasonsOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchSeason', 'selectedSeasonId', 'searchSeasonDropdown',
                    view.querySelector('#searchSeason').value,
                    (itemData) => this.onSeasonSelect(view, itemData),
                    () => this.loadMoreSeasons(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more seasons.' });
            });
        }

        loadMoreEpisodes(view) {
            if (!this.currentSeasonId) return;
            
            const seriesId = view.querySelector('#selectedItemId').value;
            
            loading.show();
            loadEpisodes(seriesId, this.currentSeasonId, this.episodesOffset, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;
                
                const newItems = episodes.map(episode => {
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    return {
                        id: episode.Id,
                        text: `${seasonNum}${epNum} - ${episode.Name}`,
                        name: episode.Name
                    };
                });
                
                this.searchEpisode_items = this.searchEpisode_items.concat(newItems);
                this.episodesOffset += episodes.length;
                
                this.searchEpisode_metadata = {
                    hasMore: this.episodesOffset < totalCount,
                    currentCount: this.episodesOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchEpisode', 'selectedEpisodeId', 'searchEpisodeDropdown',
                    view.querySelector('#searchEpisode').value,
                    (itemData) => this.onEpisodeSelect(view, itemData),
                    () => this.loadMoreEpisodes(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error loading more episodes.' });
            });
        }

        showSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'block';
            view.querySelector('#episodeContainer').style.display = 'block';
        }

        hideSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'none';
            view.querySelector('#episodeContainer').style.display = 'none';
            
            this.searchSeason_items = [];
            this.searchEpisode_items = [];
            
            view.querySelector('#searchSeason').value = '';
            view.querySelector('#selectedSeasonId').value = '';
            view.querySelector('#searchEpisode').value = '';
            view.querySelector('#selectedEpisodeId').value = '';
            
            view.querySelector('#searchSeasonDropdown').classList.remove('show');
            view.querySelector('#searchEpisodeDropdown').classList.remove('show');
        }

        toggleReverseProxySettings(view, useReverseProxy) {
            const proxyManagedElements = [
                'corsOriginsContainer',
                'httpsContainer',
                'certThumbprintContainer',
                'securityHeadersContainer',
                'cspContainer',
                'hstsContainer',
                'hstsMaxAgeContainer'
            ];

            proxyManagedElements.forEach(id => {
                const element = view.querySelector(`#${id}`);
                if (element) {
                    element.style.display = useReverseProxy ? 'none' : 'block';
                }
            });

            if (useReverseProxy) {
                view.querySelector('#enableHttps').checked = false;
                view.querySelector('#enableSecurityHeaders').checked = false;
                view.querySelector('#enableHsts').checked = false;
            }
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
                view.querySelector('#listenAddress').value = config.ListenAddress || 'localhost';
                view.querySelector('#useReverseProxy').checked = config.UseReverseProxy || false;
                view.querySelector('#allowedCorsOrigins').value = config.AllowedCorsOrigins || '';
                view.querySelector('#sessionExpirationMinutes').value = config.SessionExpirationMinutes || 60;
                view.querySelector('#rateLimitRequestsPerMinute').value = config.RateLimitRequestsPerMinute || 60;
                view.querySelector('#rateLimitBlockDurationMinutes').value = config.RateLimitBlockDurationMinutes || 15;
                view.querySelector('#externalServerUrl').value = config.ExternalServerUrl || '';
                view.querySelector('#embyApiKey').value = config.EmbyApiKey || '';
                
                view.querySelector('#enableHttps').checked = config.EnableHttps || false;
                view.querySelector('#httpsCertificateThumbprint').value = config.HttpsCertificateThumbprint || '';
                view.querySelector('#enableCsrfProtection').checked = config.EnableCsrfProtection !== false;
                view.querySelector('#enableSecurityHeaders').checked = config.EnableSecurityHeaders !== false;
                view.querySelector('#contentSecurityPolicy').value = config.ContentSecurityPolicy || "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
                view.querySelector('#enableHsts').checked = config.EnableHsts !== false;
                view.querySelector('#hstsMaxAge').value = config.HstsMaxAge || 31536000;
                view.querySelector('#enableAccountLockout').checked = config.EnableAccountLockout !== false;
                view.querySelector('#maxFailedLoginAttempts').value = config.MaxFailedLoginAttempts || 5;
                view.querySelector('#lockoutDurationMinutes').value = config.LockoutDurationMinutes || 15;
                view.querySelector('#lockoutWindowMinutes').value = config.LockoutWindowMinutes || 10;
                view.querySelector('#enableAuditLogging').checked = config.EnableAuditLogging !== false;
                view.querySelector('#maxAuditLogEntries').value = config.MaxAuditLogEntries || 1000;
                
                this.toggleReverseProxySettings(view, config.UseReverseProxy || false);
                
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
                        
                        this.searchContent_items = items.map(item => {
                            const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                            const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                            return {
                                id: item.Id,
                                text: `${item.Name}${year}${type}`,
                                type: item.Type,
                                name: item.Name
                            };
                        });
                        
                        this.searchContent_metadata = {
                            hasMore: false,
                            currentCount: items.length,
                            totalCount: items.length
                        };

                        if (config.SelectedItemType === 'Episode' && config.SelectedSeriesId) {
                            const seriesItem = this.searchContent_items.find(item => item.id === config.SelectedSeriesId);
                            if (seriesItem) {
                                view.querySelector('#searchContent').value = seriesItem.text;
                                view.querySelector('#selectedItemId').value = config.SelectedSeriesId;
                                view.querySelector('#selectedItemId').dataset.type = 'Series';
                                view.querySelector('#selectedItemId').dataset.name = seriesItem.name;
                            }
                            this.showSeriesControls(view);
                            
                            loadSeasons(config.SelectedSeriesId).then(seasonsResult => {
                                const seasons = seasonsResult.Items || [];
                                
                                this.searchSeason_items = seasons.map(season => {
                                    const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                                    return {
                                        id: season.Id,
                                        text: `${season.Name || 'Season' + seasonNum}`,
                                        name: season.Name || 'Season' + seasonNum
                                    };
                                });
                                
                                this.searchSeason_metadata = {
                                    hasMore: false,
                                    currentCount: seasons.length,
                                    totalCount: seasons.length
                                };

                                if (config.SelectedSeasonId) {
                                    const seasonItem = this.searchSeason_items.find(item => item.id === config.SelectedSeasonId);
                                    if (seasonItem) {
                                        view.querySelector('#searchSeason').value = seasonItem.text;
                                        view.querySelector('#selectedSeasonId').value = config.SelectedSeasonId;
                                    }
                                    
                                    loadEpisodes(config.SelectedSeriesId, config.SelectedSeasonId).then(episodesResult => {
                                        const episodes = episodesResult.Items || [];
                                        
                                        this.searchEpisode_items = episodes.map(episode => {
                                            const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                                            const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                                            return {
                                                id: episode.Id,
                                                text: `${seasonNum}${epNum} - ${episode.Name}`,
                                                name: episode.Name
                                            };
                                        });
                                        
                                        this.searchEpisode_metadata = {
                                            hasMore: false,
                                            currentCount: episodes.length,
                                            totalCount: episodes.length
                                        };

                                        if (config.SelectedItemId) {
                                            const episodeItem = this.searchEpisode_items.find(item => item.id === config.SelectedItemId);
                                            if (episodeItem) {
                                                view.querySelector('#searchEpisode').value = episodeItem.text;
                                                view.querySelector('#selectedEpisodeId').value = config.SelectedItemId;
                                            }
                                        }
                                    });
                                }
                            });
                        } else if (config.SelectedItemId) {
                            const selectedItem = this.searchContent_items.find(item => item.id === config.SelectedItemId);
                            if (selectedItem) {
                                view.querySelector('#searchContent').value = selectedItem.text;
                                view.querySelector('#selectedItemId').value = config.SelectedItemId;
                                view.querySelector('#selectedItemId').dataset.type = selectedItem.type;
                                view.querySelector('#selectedItemId').dataset.name = selectedItem.name;
                            }
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
            const itemInput = view.querySelector('#selectedItemId');
            const itemId = itemInput.value;
            
            if (!itemId) {
                loading.hide();
                toast({ type: 'error', text: 'Please select content to watch.' });
                return;
            }

            const itemType = itemInput.dataset.type || null;
            
            let finalItemId = itemId;
            let finalItemName = view.querySelector('#searchContent').value;
            let finalItemType = itemType;
            let seriesId = null;
            let seasonId = null;

            if (itemType === 'Series') {
                const episodeInput = view.querySelector('#selectedEpisodeId');
                const episodeId = episodeInput.value;
                
                if (!episodeId) {
                    loading.hide();
                    toast({ type: 'error', text: 'Please select an episode for TV shows.' });
                    return;
                }

                finalItemId = episodeId;
                finalItemType = 'Episode';
                seriesId = itemId;
                seasonId = view.querySelector('#selectedSeasonId').value;
                finalItemName = view.querySelector('#searchEpisode').value;
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
                    const itemSelect = view.querySelector('#selectedItemId');
                    itemSelect.innerHTML = '<option value="">Select a library first...</option>';
                    delete itemSelect._allOptions;
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
                config.ListenAddress = view.querySelector('#listenAddress').value.trim() || 'localhost';
                config.UseReverseProxy = view.querySelector('#useReverseProxy').checked;
                config.AllowedCorsOrigins = view.querySelector('#allowedCorsOrigins').value.trim();
                config.SessionExpirationMinutes = parseInt(view.querySelector('#sessionExpirationMinutes').value) || 60;
                config.RateLimitRequestsPerMinute = parseInt(view.querySelector('#rateLimitRequestsPerMinute').value) || 60;
                config.RateLimitBlockDurationMinutes = parseInt(view.querySelector('#rateLimitBlockDurationMinutes').value) || 15;
                config.ExternalServerUrl = view.querySelector('#externalServerUrl').value.trim();
                config.EmbyApiKey = view.querySelector('#embyApiKey').value.trim();
                
                config.EnableHttps = view.querySelector('#enableHttps').checked;
                config.HttpsCertificateThumbprint = view.querySelector('#httpsCertificateThumbprint').value.trim();
                config.EnableCsrfProtection = view.querySelector('#enableCsrfProtection').checked;
                config.EnableSecurityHeaders = view.querySelector('#enableSecurityHeaders').checked;
                config.ContentSecurityPolicy = view.querySelector('#contentSecurityPolicy').value.trim();
                config.EnableHsts = view.querySelector('#enableHsts').checked;
                config.HstsMaxAge = parseInt(view.querySelector('#hstsMaxAge').value) || 31536000;
                config.EnableAccountLockout = view.querySelector('#enableAccountLockout').checked;
                config.MaxFailedLoginAttempts = parseInt(view.querySelector('#maxFailedLoginAttempts').value) || 5;
                config.LockoutDurationMinutes = parseInt(view.querySelector('#lockoutDurationMinutes').value) || 15;
                config.LockoutWindowMinutes = parseInt(view.querySelector('#lockoutWindowMinutes').value) || 10;
                config.EnableAuditLogging = view.querySelector('#enableAuditLogging').checked;
                config.MaxAuditLogEntries = parseInt(view.querySelector('#maxAuditLogEntries').value) || 1000;
                
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
