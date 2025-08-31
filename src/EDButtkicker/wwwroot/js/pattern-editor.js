// Pattern Editor Wizard JavaScript
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
        
        if (this.currentStep < this.totalSteps) {
            this.currentStep++;
            this.showStep(this.currentStep);
            this.updateProgress();
        }
    }

    previousStep() {
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
                if (!this.selectedShip) {
                    this.showError('Please select a ship to continue');
                    return false;
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

    // Step 1: Ship Selection
    renderStep1() {
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
                <div class="ship-class">${ship.class} Ship</div>
            </div>
        `).join('');
    }

    selectShip(shipType) {
        this.selectedShip = shipType;
        this.renderStep1();
        document.getElementById('step1Next').disabled = false;
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

            const response = await fetch('/api/PatternEditor/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ 
                    pattern: {
                        ...template.defaultSettings,
                        pattern: patternType
                    }
                })
            });

            if (response.ok) {
                this.showSuccess(`Testing ${template.name} pattern`);
            } else {
                throw new Error('Failed to test pattern');
            }
        } catch (error) {
            console.error('Failed to test pattern:', error);
            this.showError('Failed to test pattern');
        }
    }

    updateStep3NextButton() {
        const hasAllPatterns = this.selectedEvents.every(event => this.eventPatterns[event]);
        document.getElementById('step3Next').disabled = !hasAllPatterns;
    }

    // Step 4: Fine-tuning
    renderStep4() {
        const container = document.getElementById('tuningPanel');
        
        container.innerHTML = this.selectedEvents.map(eventType => {
            const event = this.eventDefinitions[eventType];
            const patternType = this.eventPatterns[eventType];
            const template = this.templates.find(t => t.pattern === patternType);
            const settings = template ? template.defaultSettings : this.getDefaultSettings();
            
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
                <input type="range" class="control-slider" min="100" max="3000" 
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
            const settings = this.eventSettings?.[eventType] || this.getDefaultSettingsForEvent(eventType);
            const patternType = this.eventPatterns[eventType];
            
            const response = await fetch('/api/PatternEditor/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    pattern: {
                        ...settings,
                        pattern: patternType
                    }
                })
            });

            if (response.ok) {
                this.showSuccess(`Testing ${this.eventDefinitions[eventType].title}`);
            } else {
                throw new Error('Failed to test pattern');
            }
        } catch (error) {
            console.error('Failed to test event pattern:', error);
            this.showError('Failed to test pattern');
        }
    }

    // Step 5: Save
    renderStep5() {
        // Pre-populate fields if available
        const shipName = this.formatShipName(this.selectedShip);
        document.getElementById('finalPackName').value = `${shipName} Custom Patterns`;
        
        // Render summary
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

            // Build pattern data
            const patternData = {
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
                const settings = this.eventSettings?.[eventType] || this.getDefaultSettingsForEvent(eventType);
                patternData.ships[this.selectedShip].events[eventType] = {
                    ...settings,
                    pattern: this.eventPatterns[eventType],
                    name: eventType
                };
            });

            // Save pattern
            const response = await fetch('/api/PatternEditor/save', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    patternFile: patternData,
                    saveToCustom: true
                })
            });

            if (response.ok) {
                const result = await response.json();
                this.showSuccess(`Pattern pack "${packName}" saved successfully!`);
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
        
        // Hide success screen and show first step
        document.getElementById('stepSuccess').style.display = 'none';
        this.showStep(1);
        this.updateProgress();
        
        // Reset form fields
        document.getElementById('finalPackName').value = '';
        document.getElementById('finalDescription').value = '';
        document.getElementById('shipSearch').value = '';
        
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
        
        if (smallShips.includes(shipType.toLowerCase())) return 'Small';
        if (largeShips.includes(shipType.toLowerCase())) return 'Large';
        return 'Medium';
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
});