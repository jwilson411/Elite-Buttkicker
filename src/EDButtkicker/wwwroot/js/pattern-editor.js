// Pattern Editor Wizard JavaScript

// Helper function for formatting server errors
async function formatServerError(response) {
    const status = response.status;
    const text = await response.text();
    let msg = `[${status}] Request failed`;
    try {
        const data = JSON.parse(text);
        msg = data.error || data.details || data.message || data.title || data.detail || msg;
    } catch {
        if (text) msg = `[${status}] ${text}`;
    }
    if (!msg.startsWith(`[${status}]`)) msg = `[${status}] ${msg}`;
    return msg;
}

class PatternWizard {
    constructor() {
        this.currentStep = 1;
        this.totalSteps = 5;
        this.selectedShip = null;
        this.selectedEvents = [];
        this.eventPatterns = {};
        this.templates = [];
        this.shipTypes = [];
        this.eventTypes = [];
        this.eventDefinitions = {};

        // Edit mode properties
        this.isEditMode = false;
        this.loadedPatternFile = null;
        this.originalFileName = null;
        this.eventSettings = {};

        // Timeline editor properties
        this.timelineEditors = {};
        this.advancedMode = false;
        this.advancedPatterns = {};
        this.currentTimelineEditor = null;
        this.currentTimelineEvent = null;

        this.init();
    }

    async init() {
        try {
            await this.loadData();
            this.setupEventListeners();
            this.renderStep1();
        } catch (error) {
            console.error('Failed to initialize wizard:', error);
            this.showError('Failed to initialize pattern wizard');
        }
    }

    async loadData() {
        try {
            const response = await fetch('/api/PatternEditor/templates');
            const data = await response.json();
            
            this.templates = data.basicPatterns || [];
            this.shipTypes = data.shipTypes || [];
            this.eventTypes = data.eventTypes || [];
            
            // Define user-friendly event descriptions - populated from all available events
            this.eventDefinitions = {};
            
            // First, create definitions for all events from the server data
            this.eventTypes.forEach(eventType => {
                this.eventDefinitions[eventType] = this.createEventDefinition(eventType);
            });

            // Add popular events based on frequency
            const popularEvents = ['FSDJump', 'Docked', 'HullDamage', 'HeatWarning', 'ShipTargeted'];
            popularEvents.forEach(eventType => {
                if (this.eventDefinitions[eventType]) {
                    this.eventDefinitions[eventType].category = 'popular';
                }
            });

        } catch (error) {
            console.error('Failed to load wizard data:', error);
            throw error;
        }
    }

    setupEventListeners() {
        // Ship search
        document.getElementById('shipSearch').addEventListener('input', (e) => {
            this.filterShips(e.target.value);
        });

        // Event search
        document.getElementById('eventSearch').addEventListener('input', (e) => {
            this.filterEvents(e.target.value);
        });

        // Load saved author
        const savedAuthor = localStorage.getItem('patternEditorAuthor');
        if (savedAuthor) {
            document.getElementById('finalAuthor').value = savedAuthor;
        }

        // Save author on change
        document.getElementById('finalAuthor').addEventListener('input', (e) => {
            localStorage.setItem('patternEditorAuthor', e.target.value);
        });

        // Pre-populate author input for file selection
        const authorInput = document.getElementById('authorInput');
        if (authorInput && savedAuthor) {
            authorInput.value = savedAuthor;
        }

        // Setup timeline editor mode switching
        this.setupTimelineEditorListeners();
    }

    setupTimelineEditorListeners() {
        document.addEventListener('click', (e) => {
            if (e.target.classList.contains('mode-btn')) {
                const mode = e.target.getAttribute('data-mode');
                this.switchEditorMode(mode);
            }
        });
    }

    createEventDefinition(eventType) {
        // Known event definitions with friendly names and categories
        const knownEvents = {
            'FSDJump': { 
                icon: '🚀', 
                title: 'Hyperspace Jump', 
                description: 'When you jump between star systems',
                category: 'exploration'
            },
            'Docked': { 
                icon: '🏗️', 
                title: 'Station Docking', 
                description: 'When you successfully dock at a station or outpost',
                category: 'popular'
            },
            'Undocked': { 
                icon: '🚀', 
                title: 'Station Undocking', 
                description: 'When you undock from a station or outpost',
                category: 'exploration'
            },
            'HullDamage': { 
                icon: '💥', 
                title: 'Taking Hull Damage', 
                description: 'When your ship\'s hull takes damage from weapons or impacts',
                category: 'combat'
            },
            'ShipTargeted': { 
                icon: '🎯', 
                title: 'Target Acquired', 
                description: 'When you lock onto a target or are targeted by enemies',
                category: 'combat'
            },
            'FighterDestroyed': { 
                icon: '💀', 
                title: 'Ship Destroyed', 
                description: 'When you destroy an enemy ship or your ship is destroyed',
                category: 'combat'
            },
            'TouchDown': { 
                icon: '🌍', 
                title: 'Planetary Landing', 
                description: 'When you land on a planet or moon surface',
                category: 'exploration'
            },
            'Liftoff': { 
                icon: '🚁', 
                title: 'Planetary Takeoff', 
                description: 'When you lift off from a planet or moon surface',
                category: 'exploration'
            },
            'FuelScoop': { 
                icon: '☀️', 
                title: 'Fuel Scooping', 
                description: 'When you\'re scooping fuel from a star',
                category: 'exploration'
            },
            'HeatWarning': { 
                icon: '🌡️', 
                title: 'Overheating Warning', 
                description: 'When your ship temperature gets dangerously high',
                category: 'popular'
            },
            'Market': { 
                icon: '💰', 
                title: 'Trading Activity', 
                description: 'When buying or selling commodities at markets',
                category: 'trading'
            },
            'UnderAttack': { 
                icon: '⚔️', 
                title: 'Under Attack', 
                description: 'When enemies are attacking your ship',
                category: 'combat'
            },
            'FSDChargingJump': { 
                icon: '⚡', 
                title: 'FSD Charging', 
                description: 'When your FSD is charging for a jump',
                category: 'exploration'
            },
            'StartJump': { 
                icon: '🌟', 
                title: 'Jump Started', 
                description: 'When you begin a hyperspace jump',
                category: 'exploration'
            },
            'SupercruiseEntry': { 
                icon: '🌌', 
                title: 'Supercruise Entry', 
                description: 'When you enter supercruise',
                category: 'exploration'
            },
            'SupercruiseExit': { 
                icon: '🎯', 
                title: 'Supercruise Exit', 
                description: 'When you drop out of supercruise',
                category: 'exploration'
            },
            'ReceiveText': { 
                icon: '💬', 
                title: 'Message Received', 
                description: 'When you receive a text message',
                category: 'other'
            },
            'Friends': { 
                icon: '👥', 
                title: 'Friend Activity', 
                description: 'When friends come online or send messages',
                category: 'other'
            },
            'Music': { 
                icon: '🎵', 
                title: 'Music Events', 
                description: 'When music starts, stops, or changes',
                category: 'other'
            },
            'DockingRequested': { 
                icon: '🏗️', 
                title: 'Docking Requested', 
                description: 'When you request docking permission',
                category: 'exploration'
            },
            'DockingGranted': { 
                icon: '✅', 
                title: 'Docking Granted', 
                description: 'When docking permission is granted',
                category: 'exploration'
            },
            'DockingDenied': { 
                icon: '❌', 
                title: 'Docking Denied', 
                description: 'When docking permission is denied',
                category: 'exploration'
            },
            'LaunchFighter': { 
                icon: '🚁', 
                title: 'Launch Fighter', 
                description: 'When you launch a ship-launched fighter',
                category: 'combat'
            },
            'DockFighter': { 
                icon: '🏗️', 
                title: 'Dock Fighter', 
                description: 'When your fighter docks back to the mothership',
                category: 'combat'
            },
            'CargoScoop': { 
                icon: '📦', 
                title: 'Cargo Scoop', 
                description: 'When you deploy or retract the cargo scoop',
                category: 'trading'
            },
            'CollectCargo': { 
                icon: '📋', 
                title: 'Collect Cargo', 
                description: 'When you collect cargo or materials',
                category: 'trading'
            },
            'EjectCargo': { 
                icon: '📤', 
                title: 'Eject Cargo', 
                description: 'When you jettison cargo',
                category: 'trading'
            },
            'Scan': { 
                icon: '📡', 
                title: 'Scanner Activity', 
                description: 'When you complete scans of objects',
                category: 'exploration'
            },
            'Screenshot': { 
                icon: '📷', 
                title: 'Screenshot Taken', 
                description: 'When you take a screenshot',
                category: 'other'
            },
            'Died': { 
                icon: '💀', 
                title: 'Ship Destroyed', 
                description: 'When your ship is destroyed',
                category: 'combat'
            },
            'Resurrect': { 
                icon: '🔄', 
                title: 'Respawned', 
                description: 'When you respawn after ship destruction',
                category: 'other'
            }
        };

        // Return known definition or create a generic one
        if (knownEvents[eventType]) {
            return knownEvents[eventType];
        } else {
            // Create generic definition for unknown events
            return {
                icon: '⚙️',
                title: this.formatEventName(eventType),
                description: `Game event: ${this.formatEventName(eventType)}`,
                category: 'other'
            };
        }
    }

    formatEventName(eventType) {
        // Convert camelCase or PascalCase to readable format
        return eventType
            .replace(/([A-Z])/g, ' $1')
            .replace(/^./, str => str.toUpperCase())
            .trim();
    }

    // Step Navigation
    nextStep() {
        if (!this.validateCurrentStep()) return;

        // Save advanced patterns when leaving step 4
        if (this.currentStep === 4 && this.advancedMode && this.currentTimelineEditor && this.currentTimelineEvent) {
            this.advancedPatterns[this.currentTimelineEvent] = this.currentTimelineEditor.getHapticPattern();
        }

        if (this.currentStep < this.totalSteps) {
            this.currentStep++;
            this.showStep(this.currentStep);
            this.updateProgress();
        }
    }

    previousStep() {
        // Save advanced patterns when leaving step 4
        if (this.currentStep === 4 && this.advancedMode && this.currentTimelineEditor && this.currentTimelineEvent) {
            this.advancedPatterns[this.currentTimelineEvent] = this.currentTimelineEditor.getHapticPattern();
        }

        if (this.currentStep > 1) {
            this.currentStep--;
            this.showStep(this.currentStep);
            this.updateProgress();
        }
    }

    showStep(stepNumber) {
        // Hide all steps
        document.querySelectorAll('.wizard-step').forEach(step => {
            step.classList.remove('active');
        });

        // Show current step
        document.getElementById(`step${stepNumber}`).classList.add('active');

        // Render step content
        switch (stepNumber) {
            case 1: this.renderStep1(); break;
            case 2: this.renderStep2(); break;
            case 3: this.renderStep3(); break;
            case 4: this.renderStep4(); break;
            case 5: this.renderStep5(); break;
        }
    }

    updateProgress() {
        document.querySelectorAll('.progress-step').forEach((step, index) => {
            step.classList.remove('active', 'completed');
            
            if (index + 1 < this.currentStep) {
                step.classList.add('completed');
            } else if (index + 1 === this.currentStep) {
                step.classList.add('active');
            }
        });
    }

    validateCurrentStep() {
        switch (this.currentStep) {
            case 1:
                if (this.isEditMode) {
                    if (!this.loadedPatternFile) {
                        this.showError('Please load a pattern file to continue');
                        return false;
                    }
                } else {
                    if (!this.selectedShip) {
                        this.showError('Please select a ship to continue');
                        return false;
                    }
                }
                break;
            case 2:
                if (this.selectedEvents.length === 0) {
                    this.showError('Please select at least one event to continue');
                    return false;
                }
                break;
            case 3:
                const hasAllPatterns = this.selectedEvents.every(event => this.eventPatterns[event]);
                if (!hasAllPatterns) {
                    this.showError('Please choose a pattern for each selected event');
                    return false;
                }
                break;
            case 4:
                if (this.advancedMode) {
                    return this.validateAdvancedPatterns();
                }
                break;
            case 5:
                const packName = document.getElementById('finalPackName').value.trim();
                const author = document.getElementById('finalAuthor').value.trim();
                if (!packName) {
                    this.showError('Please enter a pattern pack name');
                    return false;
                }
                if (!author) {
                    this.showError('Please enter your username');
                    return false;
                }
                break;
        }
        return true;
    }

    validateAdvancedPatterns() {
        const validationErrors = [];
        const validationContainer = this.getOrCreateValidationContainer();

        // Save current timeline editor pattern if active
        if (this.currentTimelineEditor && this.currentTimelineEvent) {
            this.advancedPatterns[this.currentTimelineEvent] = this.currentTimelineEditor.getHapticPattern();
        }

        // Validate each selected event
        for (const eventType of this.selectedEvents) {
            const pattern = this.advancedPatterns[eventType] || (this.currentTimelineEvent === eventType ? this.currentTimelineEditor?.getHapticPattern() : null);

            if (!pattern) {
                validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: No advanced pattern defined`);
                continue;
            }

            // Validate pattern has at least one layer
            if (!pattern.Layers || pattern.Layers.length === 0) {
                validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Pattern must have at least one layer`);
                continue;
            }

            // Validate layer properties
            for (let i = 0; i < pattern.Layers.length; i++) {
                const layer = pattern.Layers[i];
                const layerName = `${this.eventDefinitions[eventType]?.title || eventType} Layer ${i + 1}`;

                if (!layer.Frequency || layer.Frequency < 10 || layer.Frequency > 200) {
                    validationErrors.push(`${layerName}: Frequency must be between 10-200 Hz`);
                }

                if (layer.Amplitude < 0 || layer.Amplitude > 1) {
                    validationErrors.push(`${layerName}: Amplitude must be between 0-1`);
                }

                // Calculate effective duration (0 means use pattern duration)
                const layerEffectiveDuration = layer.Duration === 0 ?
                    pattern.Duration - layer.StartTime : layer.Duration;

                if (layer.Duration > 0 && layer.Duration > pattern.Duration) {
                    validationErrors.push(`${layerName}: Layer duration cannot exceed pattern duration`);
                }

                if (layer.StartTime < 0 || layer.StartTime >= pattern.Duration) {
                    validationErrors.push(`${layerName}: Start time must be within pattern duration`);
                }

                if ((layer.StartTime + layerEffectiveDuration) > pattern.Duration) {
                    const durationDisplay = layer.Duration === 0 ?
                        'auto (pattern duration)' : `${layer.Duration}ms`;
                    validationErrors.push(`${layerName}: Layer (${durationDisplay}) extends beyond pattern duration`);
                }
            }

            // Validate custom curve points
            if (pattern.IntensityCurve === 'Custom') {
                if (!pattern.CustomCurvePoints || pattern.CustomCurvePoints.length < 2) {
                    validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Custom curve requires at least 2 points`);
                } else {
                    const points = pattern.CustomCurvePoints;

                    // Check first point starts at 0 and last ends at 1
                    if (points[0].Time !== 0) {
                        validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: First curve point must start at time 0`);
                    }
                    if (points[points.length - 1].Time !== 1) {
                        validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Last curve point must end at time 1`);
                    }

                    // Check points are monotonic increasing by time
                    for (let i = 1; i < points.length; i++) {
                        if (points[i].Time <= points[i - 1].Time) {
                            validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Curve points must be ordered by increasing time`);
                            break;
                        }
                    }

                    // Check points are within valid ranges
                    for (let i = 0; i < points.length; i++) {
                        const point = points[i];
                        if (point.Time < 0 || point.Time > 1) {
                            validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Curve point ${i + 1} time must be between 0-1`);
                        }
                        if (point.Intensity < 0 || point.Intensity > 1) {
                            validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Curve point ${i + 1} intensity must be between 0-1`);
                        }
                    }
                }
            }

            // Validate overall pattern properties
            if (pattern.Duration <= 0 || pattern.Duration > 30000) {
                validationErrors.push(`${this.eventDefinitions[eventType]?.title || eventType}: Duration must be between 1-30000ms`);
            }
        }

        // Display validation results
        this.displayValidationResults(validationContainer, validationErrors);

        return validationErrors.length === 0;
    }

    getOrCreateValidationContainer() {
        let container = document.getElementById('advancedValidation');
        if (!container) {
            container = document.createElement('div');
            container.id = 'advancedValidation';
            container.className = 'validation-panel';

            const step4 = document.getElementById('step4');
            const stepActions = step4.querySelector('.step-actions');
            step4.insertBefore(container, stepActions);
        }
        return container;
    }

    displayValidationResults(container, errors) {
        if (errors.length === 0) {
            container.innerHTML = `
                <div class="validation-success">
                    <i class="fas fa-check-circle"></i>
                    All advanced patterns are valid and ready to save.
                </div>
            `;
            container.style.display = 'block';
        } else {
            container.innerHTML = `
                <h4>Pattern Validation Errors:</h4>
                ${errors.map(error => `<div class="validation-error"><i class="fas fa-exclamation-triangle"></i> ${error}</div>`).join('')}
                <div class="validation-help">
                    <i class="fas fa-info-circle"></i>
                    Please fix these issues before proceeding. Switch between events using the dropdown above to edit each pattern.
                </div>
            `;
            container.style.display = 'block';
        }
    }

    // Mode toggle functionality
    toggleMode(mode) {
        this.isEditMode = (mode === 'edit');

        if (this.isEditMode) {
            this.showFileSelection();
            this.hideShipSelection();
        } else {
            this.hideFileSelection();
            this.showShipSelection();
            this.loadedPatternFile = null;
            this.originalFileName = null;
            this.selectedShip = null;
            this.selectedEvents = [];
            this.eventPatterns = {};
            this.eventSettings = {};
            this.renderStep1();
        }

        this.updateStep1NextButton();
    }

    showFileSelection() {
        document.getElementById('fileSelectionSection').style.display = 'block';
    }

    hideFileSelection() {
        document.getElementById('fileSelectionSection').style.display = 'none';
        document.getElementById('fileListContainer').style.display = 'none';
        document.getElementById('userFilesList').innerHTML = '';
    }

    showShipSelection() {
        document.getElementById('shipSelectionSection').style.display = 'block';
    }

    hideShipSelection() {
        document.getElementById('shipSelectionSection').style.display = 'none';
    }

    // Load user's pattern files
    async loadUserFiles() {
        const authorInput = document.getElementById('authorInput');
        const author = authorInput.value.trim();

        if (!author) {
            this.showError('Please enter your username to load your files');
            return;
        }

        try {
            const response = await fetch(`/api/PatternEditor/user-files/${encodeURIComponent(author)}`);
            if (!response.ok) {
                throw new Error(await formatServerError(response));
            }

            const data = await response.json();
            const files = Array.isArray(data) ? data : data.files || [];
            this.renderFileList(files);
            document.getElementById('fileListContainer').style.display = 'block';

        } catch (error) {
            console.error('Failed to load user files:', error);
            this.showError(`Failed to load files: ${error.message}`);
        }
    }

    // Render the list of available pattern files
    renderFileList(files) {
        const container = document.getElementById('userFilesList');

        if (!files || files.length === 0) {
            container.innerHTML = '<div style="padding: 1rem; text-align: center; color: var(--text-secondary);">No pattern files found for this user.</div>';
            return;
        }

        container.innerHTML = files.map(file => this.renderFileItem(file)).join('');
    }

    // Render individual file item
    renderFileItem(file) {
        const lastModified = file.lastModified ? new Date(file.lastModified).toLocaleDateString() : 'Unknown';
        const shipCount = file.shipCount || 'Unknown';
        const title = file.packName || file.fileName;

        return `
            <div class="file-item" onclick="wizard.selectPatternFile('${file.fileName}')">
                <div class="file-name">${title}</div>
                <div class="file-details">
                    Ships: ${shipCount} | Modified: ${lastModified}
                    ${file.description ? ` | ${file.description}` : ''}
                </div>
            </div>
        `;
    }

    // Load selected pattern file
    async selectPatternFile(fileName) {
        try {
            const response = await fetch(`/api/PatternEditor/load/${encodeURIComponent(fileName)}`);
            if (!response.ok) {
                throw new Error(await formatServerError(response));
            }

            const patternData = await response.json();
            this.populateWizardFromPattern(patternData, fileName);

        } catch (error) {
            console.error('Failed to load pattern file:', error);
            this.showError(`Failed to load pattern: ${error.message}`);
        }
    }

    // Populate wizard state from loaded pattern data
    populateWizardFromPattern(patternData, fileName) {
        this.loadedPatternFile = patternData;
        this.originalFileName = fileName;

        // Get the first ship from the pattern (assuming single ship for wizard)
        const shipKeys = Object.keys(patternData.ships || {});
        if (shipKeys.length === 0) {
            this.showError('No ship data found in pattern file');
            return;
        }

        // Show notice for multi-ship patterns
        if (shipKeys.length > 1) {
            this.showSuccess(`Multi-ship pattern loaded. Editing first ship: ${this.formatShipName(shipKeys[0])}. Other ships: ${shipKeys.slice(1).map(s => this.formatShipName(s)).join(', ')}`);
        }

        const firstShipKey = shipKeys[0];
        const shipData = patternData.ships[firstShipKey];

        // Set selected ship
        this.selectedShip = firstShipKey;

        // Set selected events and patterns
        this.selectedEvents = Object.keys(shipData.events || {});
        this.eventPatterns = {};
        this.eventSettings = {};

        Object.entries(shipData.events || {}).forEach(([eventType, eventData]) => {
            this.eventPatterns[eventType] = eventData.pattern;
            this.eventSettings[eventType] = {
                frequency: eventData.frequency || 40,
                intensity: eventData.intensity || 70,
                duration: eventData.duration || 500,
                fadeIn: eventData.fadeIn || 0,
                fadeOut: eventData.fadeOut || 0
            };
        });

        // Add missing eventDefinitions for loaded events to prevent undefined access in later steps
        this.selectedEvents.forEach(eventType => {
            if (!this.eventDefinitions[eventType]) {
                this.eventDefinitions[eventType] = this.createEventDefinition(eventType);
            }
        });

        // Pre-populate save form with existing metadata
        if (patternData.metadata) {
            document.getElementById('finalPackName').value = patternData.metadata.name || '';
            document.getElementById('finalAuthor').value = patternData.metadata.author || '';
            document.getElementById('finalDescription').value = patternData.metadata.description || '';
        }

        this.showSuccess(`Loaded pattern: ${patternData.metadata?.name || fileName}`);
        this.updateStep1NextButton();

        // Navigate to step 2 if we have a ship selected
        if (this.selectedShip) {
            setTimeout(() => {
                this.nextStep();
            }, 500);
        }
    }

    // Update step 1 next button based on mode
    updateStep1NextButton() {
        const nextBtn = document.getElementById('step1Next');

        if (this.isEditMode) {
            // In edit mode, enable if we have loaded a pattern
            nextBtn.disabled = !this.loadedPatternFile;
        } else {
            // In create mode, enable if we have selected a ship
            nextBtn.disabled = !this.selectedShip;
        }
    }

    // Step 1: Ship Selection
    renderStep1() {
        // Only render ship grid if not in edit mode or if we need to show it
        if (!this.isEditMode) {
            const grid = document.getElementById('shipGrid');
            const ships = this.shipTypes.map(shipType => ({
                type: shipType,
                name: this.formatShipName(shipType),
                class: this.determineShipClass(shipType)
            }));

            // Sort ships: popular ones first, then alphabetically
            const popularShips = ['sidewinder', 'cobra_mkiii', 'asp_explorer', 'python', 'anaconda', 'fer_de_lance'];
            ships.sort((a, b) => {
                const aPopular = popularShips.includes(a.type.toLowerCase());
                const bPopular = popularShips.includes(b.type.toLowerCase());

                if (aPopular && !bPopular) return -1;
                if (!aPopular && bPopular) return 1;
                return a.name.localeCompare(b.name);
            });

            grid.innerHTML = ships.map(ship => `
                <div class="ship-card ${this.selectedShip === ship.type ? 'selected' : ''}"
                     onclick="wizard.selectShip('${ship.type}')">
                    <div class="ship-name">${ship.name}</div>
                    <div class="ship-class">${ship.class.charAt(0).toUpperCase() + ship.class.slice(1)} Ship</div>
                </div>
            `).join('');
        }

        this.updateStep1NextButton();
    }

    selectShip(shipType) {
        this.selectedShip = shipType;
        this.renderStep1();
        this.updateStep1NextButton();
    }

    filterShips(searchTerm) {
        const cards = document.querySelectorAll('.ship-card');
        cards.forEach(card => {
            const shipName = card.querySelector('.ship-name').textContent.toLowerCase();
            const matches = shipName.includes(searchTerm.toLowerCase());
            card.style.display = matches ? 'block' : 'none';
        });
    }

    filterEvents(searchTerm) {
        if (!searchTerm.trim()) {
            // Show all events and categories when search is empty
            document.querySelectorAll('.event-card').forEach(card => {
                card.style.display = 'block';
            });
            document.querySelectorAll('.category').forEach(category => {
                category.style.display = 'block';
            });
            return;
        }

        const searchLower = searchTerm.toLowerCase();
        let hasVisibleEvents = {};

        // Filter event cards
        document.querySelectorAll('.event-card').forEach(card => {
            const title = card.querySelector('.event-title').textContent.toLowerCase();
            const description = card.querySelector('.event-description').textContent.toLowerCase();
            const matches = title.includes(searchLower) || description.includes(searchLower);
            
            card.style.display = matches ? 'block' : 'none';
            
            // Track which categories have visible events
            const categoryContainer = card.closest('.category');
            if (categoryContainer && matches) {
                const categoryType = categoryContainer.getAttribute('data-category');
                hasVisibleEvents[categoryType] = true;
            }
        });

        // Show/hide category headers based on whether they have visible events
        document.querySelectorAll('.category').forEach(category => {
            const categoryType = category.getAttribute('data-category');
            category.style.display = hasVisibleEvents[categoryType] ? 'block' : 'none';
        });
    }

    // Step 2: Event Selection
    renderStep2() {
        this.renderEventCategory('popular');
        this.renderEventCategory('combat');
        this.renderEventCategory('exploration');
        this.renderEventCategory('trading');
        this.renderEventCategory('other');
        
        this.updateStep2NextButton();
    }

    renderEventCategory(category) {
        const container = document.getElementById(`${category}Events`);
        const events = Object.keys(this.eventDefinitions)
            .filter(eventType => this.eventDefinitions[eventType].category === category)
            .map(eventType => ({
                type: eventType,
                ...this.eventDefinitions[eventType]
            }));

        container.innerHTML = events.map(event => `
            <div class="event-card ${this.selectedEvents.includes(event.type) ? 'selected' : ''}" 
                 onclick="wizard.toggleEvent('${event.type}')">
                <div class="event-title">
                    <span>${event.icon}</span>
                    <span>${event.title}</span>
                </div>
                <div class="event-description">${event.description}</div>
            </div>
        `).join('');
    }

    toggleEvent(eventType) {
        const index = this.selectedEvents.indexOf(eventType);
        if (index > -1) {
            this.selectedEvents.splice(index, 1);
            delete this.eventPatterns[eventType];
        } else {
            this.selectedEvents.push(eventType);
        }
        
        this.renderStep2();
    }

    updateStep2NextButton() {
        document.getElementById('step2Next').disabled = this.selectedEvents.length === 0;
    }

    // Step 3: Pattern Selection
    renderStep3() {
        const container = document.getElementById('selectedEventsList');
        
        container.innerHTML = this.selectedEvents.map(eventType => {
            const event = this.eventDefinitions[eventType];
            return `
                <div class="event-pattern-section">
                    <div class="event-pattern-header">
                        <div class="event-pattern-title">
                            ${event.icon} ${event.title}
                        </div>
                    </div>
                    <div class="pattern-templates">
                        ${this.renderPatternTemplates(eventType)}
                    </div>
                </div>
            `;
        }).join('');

        this.updateStep3NextButton();
    }

    renderPatternTemplates(eventType) {
        return this.templates.map(template => `
            <div class="pattern-template ${this.eventPatterns[eventType] === template.pattern ? 'selected' : ''}" 
                 onclick="wizard.selectPattern('${eventType}', '${template.pattern}')">
                <div class="template-name">${template.name}</div>
                <div class="template-description">${template.description}</div>
                <button class="template-test" onclick="event.stopPropagation(); wizard.testTemplate('${template.pattern}')">
                    ▶️ Try This
                </button>
            </div>
        `).join('');
    }

    selectPattern(eventType, patternType) {
        this.eventPatterns[eventType] = patternType;
        this.renderStep3();
    }

    async testTemplate(patternType) {
        try {
            const template = this.templates.find(t => t.pattern === patternType);
            if (!template) return;

            const payload = { ...template.defaultSettings };
            delete payload.Pattern;
            // Use existing numeric pattern value from defaults

            const response = await fetch('/api/PatternEditor/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pattern: payload })
            });

            if (response.ok) {
                this.showSuccess(`Testing ${template.name} pattern`);
            } else {
                throw new Error(await formatServerError(response));
            }
        } catch (error) {
            console.error('Failed to test pattern:', error);
            this.showError(error.message);
        }
    }

    updateStep3NextButton() {
        const hasAllPatterns = this.selectedEvents.every(event => this.eventPatterns[event]);
        document.getElementById('step3Next').disabled = !hasAllPatterns;
    }

    // Step 4: Fine-tuning
    renderStep4() {
        // Initialize timeline editors if in advanced mode
        if (this.advancedMode) {
            this.initializeAllTimelineEditors();
        }

        const container = document.getElementById('tuningPanel');

        container.innerHTML = this.selectedEvents.map(eventType => {
            const event = this.eventDefinitions[eventType];
            const patternType = this.eventPatterns[eventType];
            const template = this.templates.find(t => t.pattern === patternType);
            const settings = (this.eventSettings && this.eventSettings[eventType]) || (template ? template.defaultSettings : this.getDefaultSettings());

            return `
                <div class="tuning-event">
                    <div class="tuning-header">
                        <div class="tuning-title">${event.icon} ${event.title}</div>
                        <button class="test-button" onclick="wizard.testEventPattern('${eventType}')">
                            🎧 Test Pattern
                        </button>
                    </div>
                    <div class="tuning-controls">
                        ${this.renderTuningControls(eventType, settings)}
                    </div>
                </div>
            `;
        }).join('');
    }

    renderTuningControls(eventType, settings) {
        return `
            <div class="control-group">
                <label class="control-label">Intensity</label>
                <input type="range" class="control-slider" min="10" max="100" 
                       value="${settings.intensity}" 
                       oninput="wizard.updateSetting('${eventType}', 'intensity', this.value)">
                <div class="control-value" id="${eventType}_intensity">${settings.intensity}%</div>
            </div>
            <div class="control-group">
                <label class="control-label">Frequency</label>
                <input type="range" class="control-slider" min="10" max="100" 
                       value="${settings.frequency}" 
                       oninput="wizard.updateSetting('${eventType}', 'frequency', this.value)">
                <div class="control-value" id="${eventType}_frequency">${settings.frequency}Hz</div>
            </div>
            <div class="control-group">
                <label class="control-label">Duration</label>
                <input type="range" class="control-slider" min="100" max="10000" step="100"
                       value="${settings.duration}"
                       oninput="wizard.updateSetting('${eventType}', 'duration', this.value)">
                <div class="control-value" id="${eventType}_duration">${settings.duration}ms</div>
            </div>
            <div class="control-group">
                <label class="control-label">Fade In</label>
                <input type="range" class="control-slider" min="0" max="500" 
                       value="${settings.fadeIn || 0}" 
                       oninput="wizard.updateSetting('${eventType}', 'fadeIn', this.value)">
                <div class="control-value" id="${eventType}_fadeIn">${settings.fadeIn || 0}ms</div>
            </div>
        `;
    }

    updateSetting(eventType, setting, value) {
        const numValue = parseInt(value);
        
        // Initialize event settings if they don't exist
        if (!this.eventSettings) {
            this.eventSettings = {};
        }
        if (!this.eventSettings[eventType]) {
            const patternType = this.eventPatterns[eventType];
            const template = this.templates.find(t => t.pattern === patternType);
            this.eventSettings[eventType] = { ...(template ? template.defaultSettings : this.getDefaultSettings()) };
        }
        
        this.eventSettings[eventType][setting] = numValue;
        
        // Update display
        const displayElement = document.getElementById(`${eventType}_${setting}`);
        if (displayElement) {
            const unit = setting === 'intensity' ? '%' : setting === 'frequency' ? 'Hz' : 'ms';
            displayElement.textContent = `${numValue}${unit}`;
        }
    }

    async testEventPattern(eventType) {
        try {
            const pattern = this.resolveEventPattern(eventType);

            const response = await fetch('/api/PatternEditor/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pattern: pattern })
            });

            if (response.ok) {
                this.showSuccess(`Testing ${this.eventDefinitions[eventType].title}`);
            } else {
                throw new Error(await formatServerError(response));
            }
        } catch (error) {
            console.error('Failed to test event pattern:', error);
            this.showError(error.message);
        }
    }

    // Consistent pattern resolution for testing and save flows
    resolveEventPattern(eventType) {
        if (this.advancedMode) {
            // Save current timeline editor pattern if it matches this event
            if (this.currentTimelineEditor && this.currentTimelineEvent === eventType) {
                this.advancedPatterns[eventType] = this.currentTimelineEditor.getHapticPattern();
            }

            // Return advanced pattern if available
            if (this.advancedPatterns[eventType]) {
                return this.advancedPatterns[eventType];
            }
        }

        // Build HapticPattern from simple settings
        return this.buildHapticPattern(eventType);
    }

    // Timeline Editor Integration Methods
    switchEditorMode(mode) {
        this.advancedMode = (mode === 'advanced');

        // Update UI
        document.querySelectorAll('.mode-btn').forEach(btn => {
            btn.classList.remove('active');
            if (btn.getAttribute('data-mode') === mode) {
                btn.classList.add('active');
            }
        });

        document.querySelectorAll('.editor-panel').forEach(panel => {
            panel.classList.remove('active');
        });

        if (this.advancedMode) {
            document.getElementById('advancedEditor').classList.add('active');
            this.initializeAllTimelineEditors();
        } else {
            document.getElementById('simpleEditor').classList.add('active');
        }
    }

    initializeAllTimelineEditors() {
        // Initialize timeline editor containers for each event
        const container = document.getElementById('timelineEditorContainer');

        // Clear container and create event-specific editors
        container.innerHTML = `
            <div class="timeline-event-selector">
                <label>Edit Pattern For:</label>
                <select id="eventSelector">
                    ${this.selectedEvents.map(eventType => `
                        <option value="${eventType}">${this.eventDefinitions[eventType]?.title || eventType}</option>
                    `).join('')}
                </select>
            </div>
            <div id="currentTimelineEditor" class="timeline-editor-main">
                <!-- Current event's timeline editor will be initialized here -->
            </div>
        `;

        // Set up event selector
        const eventSelector = container.querySelector('#eventSelector');
        eventSelector.addEventListener('change', (e) => {
            this.switchTimelineEvent(e.target.value);
        });

        // Initialize editor for first event
        if (this.selectedEvents.length > 0) {
            this.currentTimelineEvent = this.selectedEvents[0];
            this.initializeTimelineForEvent(this.currentTimelineEvent);
        }
    }

    initializeTimelineForEvent(eventType) {
        const editorContainer = document.getElementById('currentTimelineEditor');

        // Save current pattern if switching events
        if (this.currentTimelineEditor && this.currentTimelineEvent) {
            this.advancedPatterns[this.currentTimelineEvent] = this.currentTimelineEditor.getHapticPattern();
        }

        // Create new editor for the event
        this.currentTimelineEditor = new TimelineEditor();
        this.currentTimelineEditor.initialize(editorContainer);

        // Load existing advanced pattern or build from simple settings
        let pattern;
        if (this.advancedPatterns[eventType]) {
            pattern = this.advancedPatterns[eventType];
        } else {
            pattern = this.buildHapticPattern(eventType);
        }

        this.currentTimelineEditor.setHapticPattern(pattern);

        // Set up callbacks
        this.currentTimelineEditor.on('onPatternChanged', () => {
            this.advancedPatterns[eventType] = this.currentTimelineEditor.getHapticPattern();
            this.markPatternAsModified();
        });

        this.currentTimelineEvent = eventType;

        // Ensure canvas receives focus for keyboard navigation
        if (this.currentTimelineEditor.canvas) {
            this.currentTimelineEditor.canvas.focus();
        }
    }

    switchTimelineEvent(eventType) {
        this.initializeTimelineForEvent(eventType);

        // Update selector
        const eventSelector = document.getElementById('eventSelector');
        if (eventSelector) {
            eventSelector.value = eventType;
        }
    }

    initializeTimelineEditor(eventType, container) {
        // Legacy method - redirect to new per-event system
        this.initializeTimelineForEvent(eventType);
        return this.currentTimelineEditor;
    }

    getPatternFromEditor(eventType) {
        if (this.advancedMode) {
            // Save current timeline editor pattern if it matches the requested event
            if (this.currentTimelineEditor && this.currentTimelineEvent === eventType) {
                this.advancedPatterns[eventType] = this.currentTimelineEditor.getHapticPattern();
            }

            // Return advanced pattern if available
            if (this.advancedPatterns[eventType]) {
                return this.advancedPatterns[eventType];
            }

            // Fallback to current timeline editor if it exists
            if (this.currentTimelineEditor) {
                return this.currentTimelineEditor.getHapticPattern();
            }
        }

        // Get complete HapticPattern from simple editor using buildHapticPattern
        return this.buildHapticPattern(eventType);
    }

    loadPatternIntoEditor(eventType, pattern) {
        if (this.advancedMode) {
            // Store in advanced patterns
            this.advancedPatterns[eventType] = pattern;

            // If currently editing this event, update the timeline editor
            if (this.currentTimelineEvent === eventType && this.currentTimelineEditor) {
                this.currentTimelineEditor.setHapticPattern(pattern);
            }
        } else {
            // Load into simple editor settings
            if (pattern.Layers && pattern.Layers.length > 0) {
                // Convert complex pattern to simple settings (use first layer)
                const firstLayer = pattern.Layers[0];
                this.eventSettings[eventType] = {
                    frequency: firstLayer.Frequency || 40,
                    intensity: (firstLayer.Amplitude || 1) * 100,
                    duration: pattern.Duration || 500,
                    fadeIn: firstLayer.FadeIn || 0,
                    fadeOut: firstLayer.FadeOut || 0
                };
            } else {
                // Use basic pattern properties
                this.eventSettings[eventType] = {
                    frequency: pattern.Frequency || 40,
                    intensity: pattern.Intensity || 70,
                    duration: pattern.Duration || 500,
                    fadeIn: pattern.FadeIn || 0,
                    fadeOut: pattern.FadeOut || 0
                };
            }
        }
    }

    buildHapticPattern(eventType) {
        const settings = this.eventSettings?.[eventType] || this.getDefaultSettingsForEvent(eventType);

        // Build basic pattern structure
        const pattern = {
            Name: `${eventType} Pattern`,
            Duration: settings.duration || 500,
            Frequency: settings.frequency || 40,
            Intensity: settings.intensity || 70,
            FadeIn: settings.fadeIn || 0,
            FadeOut: settings.fadeOut || 0,
            IntensityCurve: "Linear",
            Layers: [],
            CustomCurvePoints: []
        };

        // Add default layer
        pattern.Layers.push({
            Waveform: "Sine",
            Frequency: pattern.Frequency,
            Amplitude: pattern.Intensity / 100,
            PhaseOffset: 0,
            StartTime: 0,
            Duration: pattern.Duration,
            FadeIn: pattern.FadeIn,
            FadeOut: pattern.FadeOut,
            Curve: "Linear"
        });

        // Add default curve points
        pattern.CustomCurvePoints.push(
            { Time: 0, Intensity: pattern.Intensity / 100 },
            { Time: 1, Intensity: pattern.Intensity / 100 }
        );

        return pattern;
    }

    markPatternAsModified() {
        // Visual feedback that pattern has been changed
        // Could add a small indicator or update button state
        console.log('Pattern modified');
    }

    // Step 5: Save
    renderStep5() {
        const nameInput = document.getElementById('finalPackName');
        const authorInput = document.getElementById('finalAuthor');
        const descInput = document.getElementById('finalDescription');

        if (!this.isEditMode) {
            if (!nameInput.value) nameInput.value = `${this.formatShipName(this.selectedShip)} Custom Patterns`;
        }

        this.renderSummary();
    }

    renderSummary() {
        const container = document.getElementById('patternSummary');
        const eventCount = this.selectedEvents.length;
        const shipName = this.formatShipName(this.selectedShip);
        
        container.innerHTML = `
            <div class="summary-title">Pattern Pack Summary</div>
            <div class="summary-item">
                <span>Ship:</span>
                <span>${shipName}</span>
            </div>
            <div class="summary-item">
                <span>Events:</span>
                <span>${eventCount} custom patterns</span>
            </div>
            <div class="summary-item">
                <span>Selected Events:</span>
                <span>${this.selectedEvents.map(e => this.eventDefinitions[e].title).join(', ')}</span>
            </div>
        `;
    }

    async saveWizardPattern() {
        if (!this.validateCurrentStep()) return;

        try {
            const packName = document.getElementById('finalPackName').value.trim();
            const author = document.getElementById('finalAuthor').value.trim();
            const description = document.getElementById('finalDescription').value.trim();

            let patternData;

            if (this.isEditMode && this.loadedPatternFile) {
                // Update existing pattern data
                patternData = { ...this.loadedPatternFile };

                // Update metadata
                patternData.metadata = {
                    ...patternData.metadata,
                    name: packName,
                    author: author,
                    description: description || patternData.metadata?.description || `Custom haptic patterns for ${this.formatShipName(this.selectedShip)}`,
                    lastModified: new Date().toISOString()
                    // Preserve original created date and version
                };

                // Update ship events
                const shipKey = this.selectedShip;
                if (!patternData.ships[shipKey]) {
                    patternData.ships[shipKey] = {
                        displayName: this.formatShipName(shipKey),
                        class: this.determineShipClass(shipKey),
                        role: this.determineShipRole(shipKey),
                        events: {}
                    };
                }

                // Clear existing events and add updated ones
                patternData.ships[shipKey].events = {};
                this.selectedEvents.forEach(eventType => {
                    const resolvedPattern = this.resolveEventPattern(eventType);
                    resolvedPattern.Name = eventType; // Set proper HapticPattern.Name casing
                    patternData.ships[shipKey].events[eventType] = {
                        ...resolvedPattern,
                        pattern: this.eventPatterns[eventType]
                    };
                });

            } else {
                // Build new pattern data
                patternData = {
                    metadata: {
                        name: packName,
                        author: author,
                        description: description || `Custom haptic patterns for ${this.formatShipName(this.selectedShip)}`,
                        version: '1.0.0',
                        tags: ['custom', 'wizard-generated'],
                        created: new Date().toISOString(),
                        lastModified: new Date().toISOString()
                    },
                    ships: {
                        [this.selectedShip]: {
                            displayName: this.formatShipName(this.selectedShip),
                            class: this.determineShipClass(this.selectedShip),
                            role: this.determineShipRole(this.selectedShip),
                            events: {}
                        }
                    }
                };

                // Add event patterns
                this.selectedEvents.forEach(eventType => {
                    const resolvedPattern = this.resolveEventPattern(eventType);
                    resolvedPattern.Name = eventType; // Set proper HapticPattern.Name casing
                    patternData.ships[this.selectedShip].events[eventType] = {
                        ...resolvedPattern,
                        pattern: this.eventPatterns[eventType]
                    };
                });
            }

            // Save pattern
            const response = await fetch('/api/PatternEditor/save', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    patternFile: patternData,
                    saveToCustom: true,
                    fileName: this.originalFileName // Include original filename for updates
                })
            });

            if (response.ok) {
                const result = await response.json();
                const actionWord = this.isEditMode ? 'updated' : 'saved';
                this.showSuccess(`Pattern pack "${packName}" ${actionWord} successfully!`);
                this.showSuccessScreen();
            } else {
                const error = await response.json();
                throw new Error(error.error || 'Failed to save pattern');
            }

        } catch (error) {
            console.error('Failed to save wizard pattern:', error);
            this.showError(`Failed to save pattern: ${error.message}`);
        }
    }

    showSuccessScreen() {
        document.querySelectorAll('.wizard-step').forEach(step => {
            step.classList.remove('active');
        });
        document.getElementById('stepSuccess').style.display = 'block';
        
        // Update progress bar to show completion
        document.querySelectorAll('.progress-step').forEach(step => {
            step.classList.remove('active');
            step.classList.add('completed');
        });
    }

    startOver() {
        // Reset wizard state
        this.currentStep = 1;
        this.selectedShip = null;
        this.selectedEvents = [];
        this.eventPatterns = {};
        this.eventSettings = {};

        // Reset edit mode state
        this.isEditMode = false;
        this.loadedPatternFile = null;
        this.originalFileName = null;

        // Hide success screen and show first step
        document.getElementById('stepSuccess').style.display = 'none';
        this.showStep(1);
        this.updateProgress();

        // Reset form fields
        document.getElementById('finalPackName').value = '';
        document.getElementById('finalDescription').value = '';
        document.getElementById('shipSearch').value = '';
        document.getElementById('authorInput').value = localStorage.getItem('patternEditorAuthor') || '';

        // Reset mode selection
        document.querySelector('input[name="editorMode"][value="create"]').checked = true;
        this.hideFileSelection();
        this.showShipSelection();

        // Disable next buttons
        document.getElementById('step1Next').disabled = true;
        document.getElementById('step2Next').disabled = true;
        document.getElementById('step3Next').disabled = true;
    }

    async testAllPatterns() {
        // Test each pattern in sequence
        for (const eventType of this.selectedEvents) {
            await this.testEventPattern(eventType);
            // Small delay between tests
            await new Promise(resolve => setTimeout(resolve, 1000));
        }
    }

    // Helper Methods
    formatShipName(shipType) {
        return shipType
            .split('_')
            .map(word => word.charAt(0).toUpperCase() + word.slice(1))
            .join(' ');
    }

    determineShipClass(shipType) {
        const smallShips = ['sidewinder', 'eagle', 'hauler', 'adder', 'viper', 'cobramkiii', 'viper_mkiv', 'diamondback_scout', 'imperial_courier'];
        const largeShips = ['anaconda', 'cutter', 'corvette', 'belugaliner', 'type9', 'type10'];

        if (smallShips.includes(shipType.toLowerCase())) return 'small';
        if (largeShips.includes(shipType.toLowerCase())) return 'large';
        return 'medium';
    }

    determineShipRole(shipType) {
        const combatShips = ['sidewinder', 'eagle', 'viper', 'vulture', 'fer_de_lance', 'corvette'];
        const explorerShips = ['asp', 'diamondback_explorer', 'anaconda', 'krait_phantom'];
        const traderShips = ['hauler', 'type6', 'type7', 'type9', 'type10', 'cutter'];
        
        const shipLower = shipType.toLowerCase();
        if (combatShips.includes(shipLower)) return 'combat';
        if (explorerShips.includes(shipLower)) return 'exploration';
        if (traderShips.includes(shipLower)) return 'trading';
        return 'multipurpose';
    }

    getDefaultSettings() {
        return {
            frequency: 40,
            intensity: 70,
            duration: 500,
            fadeIn: 0,
            fadeOut: 0
        };
    }

    getDefaultSettingsForEvent(eventType) {
        const patternType = this.eventPatterns[eventType];
        const template = this.templates.find(t => t.pattern === patternType);
        return template ? template.defaultSettings : this.getDefaultSettings();
    }

    showError(message) {
        // Simple alert for now - could be replaced with a toast system
        alert('Error: ' + message);
    }

    showSuccess(message) {
        console.log('Success:', message);
        // Could show a temporary toast notification
    }

    async setupForEventEdit(eventType) {
        try {
            // Set edit mode
            this.isEditMode = true;

            // Wait for ship data to load
            await this.loadData();

            // Auto-select the first available ship if none is selected
            if (!this.selectedShip && this.shipTypes.length > 0) {
                this.selectedShip = this.shipTypes[0];
            }

            // Set the specific event as selected
            this.selectedEvents = [eventType];

            // Ensure the event definition exists
            if (!this.eventDefinitions[eventType]) {
                this.eventDefinitions[eventType] = this.createEventDefinition(eventType);
            }

            // Set a default pattern if none exists
            if (!this.eventPatterns[eventType]) {
                this.eventPatterns[eventType] = 'SharpPulse';
            }

            // Skip to step 4 (pattern configuration) since we're editing a specific event
            this.showStep(4);
            this.updateProgress();

            // Initialize the timeline editor for this event
            if (this.advancedMode) {
                this.initializeTimelineForEvent(eventType);
            }

            // Update the display
            this.renderStep4();

        } catch (error) {
            console.error('Error setting up event edit:', error);
            this.showError('Failed to set up event editor: ' + error.message);
        }
    }
}

// Global functions for HTML onclick handlers
function nextStep() { wizard.nextStep(); }
function previousStep() { wizard.previousStep(); }
function saveWizardPattern() { wizard.saveWizardPattern(); }
function startOver() { wizard.startOver(); }
function testAllPatterns() { wizard.testAllPatterns(); }

// Initialize the wizard when page loads
let wizard;
document.addEventListener('DOMContentLoaded', () => {
    wizard = new PatternWizard();

    // Check for URL parameters to auto-configure for editing
    const urlParams = new URLSearchParams(window.location.search);
    const eventType = urlParams.get('event');
    const mode = urlParams.get('mode');

    if (eventType && mode === 'edit') {
        // Set up wizard for editing a specific event
        wizard.setupForEventEdit(eventType);
    }
});