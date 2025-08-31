// Pattern Editor JavaScript
class PatternEditor {
    constructor() {
        this.currentPattern = null;
        this.selectedShip = null;
        this.templates = [];
        this.shipTypes = [];
        this.eventTypes = [];
        this.isDirty = false;
        
        this.init();
    }

    async init() {
        try {
            await this.loadTemplates();
            this.setupEventListeners();
            this.createNewPattern();
            
            // Load author from localStorage if available
            const savedAuthor = localStorage.getItem('patternEditorAuthor');
            if (savedAuthor) {
                document.getElementById('author').value = savedAuthor;
                document.getElementById('authorFilter').value = savedAuthor;
                this.updateFilenamePreview();
            }
        } catch (error) {
            console.error('Failed to initialize pattern editor:', error);
            this.showError('Failed to initialize pattern editor');
        }
    }

    async loadTemplates() {
        try {
            const response = await fetch('/api/PatternEditor/templates');
            const data = await response.json();
            
            this.templates = data.basicPatterns || [];
            this.shipTypes = data.shipTypes || [];
            this.eventTypes = data.eventTypes || [];
            
            this.renderTemplates();
            this.populateShipTypeSelect();
            this.populateEventTypeSelect();
            this.populatePatternTemplateSelect();
        } catch (error) {
            console.error('Failed to load templates:', error);
            this.showError('Failed to load pattern templates');
        }
    }

    setupEventListeners() {
        // Form change listeners
        document.getElementById('packName').addEventListener('input', () => {
            this.updateFilenamePreview();
            this.markDirty();
        });
        
        document.getElementById('author').addEventListener('input', () => {
            this.updateFilenamePreview();
            this.markDirty();
            // Save author to localStorage
            localStorage.setItem('patternEditorAuthor', document.getElementById('author').value);
        });

        ['description', 'version', 'tags'].forEach(id => {
            document.getElementById(id).addEventListener('input', () => this.markDirty());
        });

        // Prevent accidental navigation when dirty
        window.addEventListener('beforeunload', (e) => {
            if (this.isDirty) {
                e.preventDefault();
                e.returnValue = '';
            }
        });
    }

    renderTemplates() {
        const grid = document.getElementById('templateGrid');
        grid.innerHTML = this.templates.map(template => `
            <div class="template-item" onclick="patternEditor.applyTemplate('${template.pattern}')">
                <div class="template-name">${template.name}</div>
                <div class="template-desc">${template.description}</div>
            </div>
        `).join('');
    }

    populateShipTypeSelect() {
        const select = document.getElementById('shipType');
        select.innerHTML = '<option value="">Select ship type...</option>' +
            this.shipTypes.map(ship => `<option value="${ship}">${ship}</option>`).join('');
    }

    populateEventTypeSelect() {
        const select = document.getElementById('eventType');
        select.innerHTML = '<option value="">Select event type...</option>' +
            this.eventTypes.map(event => `<option value="${event}">${event}</option>`).join('');
    }

    populatePatternTemplateSelect() {
        const select = document.getElementById('patternTemplate');
        select.innerHTML = '<option value="">Select template...</option>' +
            this.templates.map(template => 
                `<option value="${template.pattern}">${template.name}</option>`
            ).join('');
    }

    async createNewPattern() {
        try {
            const packName = document.getElementById('packName').value || 'New Pattern Pack';
            const author = document.getElementById('author').value || 'Unknown Author';
            
            const response = await fetch('/api/PatternEditor/create', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    packName: packName,
                    author: author,
                    description: document.getElementById('description').value,
                    tags: this.parseTags(document.getElementById('tags').value)
                })
            });

            if (response.ok) {
                const data = await response.json();
                this.currentPattern = data.patternFile;
                this.updateUI();
                this.isDirty = false;
            } else {
                throw new Error('Failed to create new pattern');
            }
        } catch (error) {
            console.error('Failed to create new pattern:', error);
            this.showError('Failed to create new pattern');
        }
    }

    async savePattern() {
        if (!this.validateRequiredFields()) {
            return;
        }

        try {
            this.updatePatternFromForm();
            
            const response = await fetch('/api/PatternEditor/save', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    patternFile: this.currentPattern,
                    saveToCustom: true
                })
            });

            if (response.ok) {
                const data = await response.json();
                this.showSuccess(`Pattern saved successfully as ${data.fileName}`);
                this.isDirty = false;
                await this.loadUserFiles();
            } else {
                const error = await response.json();
                throw new Error(error.error || 'Failed to save pattern');
            }
        } catch (error) {
            console.error('Failed to save pattern:', error);
            this.showError(`Failed to save pattern: ${error.message}`);
        }
    }

    async validatePattern() {
        try {
            this.updatePatternFromForm();
            
            const response = await fetch('/api/PatternEditor/validate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(this.currentPattern)
            });

            if (response.ok) {
                const data = await response.json();
                this.renderValidation(data);
            } else {
                throw new Error('Failed to validate pattern');
            }
        } catch (error) {
            console.error('Failed to validate pattern:', error);
            this.showError('Failed to validate pattern');
        }
    }

    renderValidation(validation) {
        const panel = document.getElementById('validationPanel');
        
        if (validation.isValid && validation.errors.length === 0 && validation.warnings.length === 0) {
            panel.innerHTML = '<div class="validation-success">✅ Pattern is valid and ready to use!</div>';
        } else {
            let html = '';
            
            if (validation.errors.length > 0) {
                html += '<div><strong>Errors:</strong></div>';
                validation.errors.forEach(error => {
                    html += `<div class="validation-error">❌ ${error}</div>`;
                });
            }
            
            if (validation.warnings.length > 0) {
                html += '<div><strong>Warnings:</strong></div>';
                validation.warnings.forEach(warning => {
                    html += `<div class="validation-warning">⚠️ ${warning}</div>`;
                });
            }
            
            if (validation.isValid) {
                html += '<div class="validation-success mt-2">✅ Pattern is valid despite warnings</div>';
            }
            
            panel.innerHTML = html;
        }
    }

    async loadUserFiles() {
        const author = document.getElementById('authorFilter').value;
        if (!author.trim()) {
            document.getElementById('userFiles').innerHTML = '<p class="text-secondary">Enter your username to see your files</p>';
            return;
        }

        try {
            const response = await fetch(`/api/PatternEditor/user-files/${encodeURIComponent(author)}`);
            
            if (response.ok) {
                const data = await response.json();
                this.renderUserFiles(data.files);
            } else {
                document.getElementById('userFiles').innerHTML = '<p class="text-secondary">No files found</p>';
            }
        } catch (error) {
            console.error('Failed to load user files:', error);
            document.getElementById('userFiles').innerHTML = '<p class="text-danger">Failed to load files</p>';
        }
    }

    async refreshPatternFiles() {
        try {
            const userFiles = document.getElementById('userFiles');
            userFiles.innerHTML = '<div class="loading"><i class="fas fa-sync-alt fa-spin"></i> Refreshing pattern files...</div>';

            // Call the reload endpoint first
            const reloadResponse = await fetch('/api/PatternFiles/reload', {
                method: 'POST'
            });

            if (!reloadResponse.ok) {
                throw new Error('Failed to reload pattern files');
            }

            const reloadData = await reloadResponse.json();
            
            // Show success message temporarily
            userFiles.innerHTML = `
                <div class="refresh-success-small">
                    <i class="fas fa-check-circle"></i>
                    <div>Files refreshed! ${reloadData.totalPacks} packs loaded</div>
                    ${reloadData.newPacks > 0 ? `<div>(${reloadData.newPacks} new packs found)</div>` : ''}
                </div>
            `;

            // Reload templates in case new patterns were added
            await this.loadTemplates();

            // Wait a moment to show the success message
            await new Promise(resolve => setTimeout(resolve, 2000));

            // Then reload the user files display
            await this.loadUserFiles();

        } catch (error) {
            console.error('Error refreshing pattern files:', error);
            document.getElementById('userFiles').innerHTML = `
                <div class="error-message-small">
                    <i class="fas fa-exclamation-triangle"></i>
                    <div>Failed to refresh files</div>
                    <div class="error-details">${error.message}</div>
                </div>
            `;
        }
    }

    renderUserFiles(files) {
        const container = document.getElementById('userFiles');
        
        if (files.length === 0) {
            container.innerHTML = '<p class="text-secondary">No pattern files found</p>';
            return;
        }

        container.innerHTML = files.map(file => `
            <div class="file-item" onclick="patternEditor.loadPattern('${file.fileName}')">
                <div class="file-name">${file.packName}</div>
                <div class="file-details">
                    ${file.fileName}<br>
                    ${file.shipCount} ships, ${file.eventCount} events<br>
                    ${new Date(file.lastModified).toLocaleDateString()}
                </div>
            </div>
        `).join('');
    }

    async loadPattern(fileName) {
        try {
            const response = await fetch(`/api/PatternEditor/load/${encodeURIComponent(fileName)}`);
            
            if (response.ok) {
                const pattern = await response.json();
                this.currentPattern = pattern;
                this.updateUI();
                this.isDirty = false;
                this.showSuccess(`Loaded pattern: ${pattern.metadata.name}`);
            } else {
                throw new Error('Failed to load pattern');
            }
        } catch (error) {
            console.error('Failed to load pattern:', error);
            this.showError('Failed to load pattern file');
        }
    }

    updateUI() {
        if (!this.currentPattern) return;

        const meta = this.currentPattern.metadata;
        document.getElementById('packName').value = meta.name || '';
        document.getElementById('author').value = meta.author || '';
        document.getElementById('description').value = meta.description || '';
        document.getElementById('version').value = meta.version || '1.0.0';
        document.getElementById('tags').value = (meta.tags || []).join(', ');

        this.renderShipList();
        this.updateFilenamePreview();
    }

    renderShipList() {
        const container = document.getElementById('shipList');
        const ships = Object.keys(this.currentPattern.ships || {});
        
        if (ships.length === 0) {
            container.innerHTML = '<div class="text-secondary">No ships added yet</div>';
            document.getElementById('eventEditor').style.display = 'none';
            return;
        }

        container.innerHTML = ships.map(shipType => {
            const ship = this.currentPattern.ships[shipType];
            const isActive = this.selectedShip === shipType;
            return `
                <div class="ship-item ${isActive ? 'active' : ''}" 
                     onclick="patternEditor.selectShip('${shipType}')">
                    <div>
                        <strong>${ship.displayName || shipType}</strong><br>
                        <small>${Object.keys(ship.events || {}).length} events</small>
                    </div>
                    <button onclick="event.stopPropagation(); patternEditor.removeShip('${shipType}')" 
                            class="btn btn-danger btn-sm">×</button>
                </div>
            `;
        }).join('');
    }

    selectShip(shipType) {
        this.selectedShip = shipType;
        this.renderShipList();
        this.renderEventEditor();
        document.getElementById('eventEditor').style.display = 'block';
    }

    renderEventEditor() {
        if (!this.selectedShip) return;

        const ship = this.currentPattern.ships[this.selectedShip];
        const events = ship.events || {};
        const container = document.getElementById('eventGrid');

        container.innerHTML = `
            <div class="section-header">
                <h4>Events for ${ship.displayName || this.selectedShip}</h4>
                <button onclick="patternEditor.showAddEventModal()" class="btn btn-secondary btn-sm">Add Event</button>
            </div>
            ${Object.keys(events).map(eventName => this.renderEventCard(eventName, events[eventName])).join('')}
        `;
    }

    renderEventCard(eventName, pattern) {
        return `
            <div class="event-card">
                <div class="event-header">
                    <span class="event-title">${eventName}</span>
                    <div>
                        <button onclick="patternEditor.testPattern('${eventName}')" class="btn btn-test btn-sm">Test</button>
                        <button onclick="patternEditor.removeEvent('${eventName}')" class="btn btn-danger btn-sm">Remove</button>
                    </div>
                </div>
                <div class="pattern-controls">
                    ${this.renderPatternControls(eventName, pattern)}
                </div>
            </div>
        `;
    }

    renderPatternControls(eventName, pattern) {
        return `
            <div class="range-control">
                <label>Frequency (${pattern.frequency}Hz)</label>
                <input type="range" min="10" max="100" value="${pattern.frequency}" 
                       onchange="patternEditor.updatePatternValue('${eventName}', 'frequency', this.value)">
                <div class="range-value">${pattern.frequency}Hz</div>
            </div>
            <div class="range-control">
                <label>Intensity (${pattern.intensity}%)</label>
                <input type="range" min="1" max="100" value="${pattern.intensity}" 
                       onchange="patternEditor.updatePatternValue('${eventName}', 'intensity', this.value)">
                <div class="range-value">${pattern.intensity}%</div>
            </div>
            <div class="range-control">
                <label>Duration (${pattern.duration}ms)</label>
                <input type="range" min="50" max="5000" value="${pattern.duration}" 
                       onchange="patternEditor.updatePatternValue('${eventName}', 'duration', this.value)">
                <div class="range-value">${pattern.duration}ms</div>
            </div>
            <div class="range-control">
                <label>Fade In (${pattern.fadeIn || 0}ms)</label>
                <input type="range" min="0" max="1000" value="${pattern.fadeIn || 0}" 
                       onchange="patternEditor.updatePatternValue('${eventName}', 'fadeIn', this.value)">
                <div class="range-value">${pattern.fadeIn || 0}ms</div>
            </div>
            <div class="range-control">
                <label>Fade Out (${pattern.fadeOut || 0}ms)</label>
                <input type="range" min="0" max="1000" value="${pattern.fadeOut || 0}" 
                       onchange="patternEditor.updatePatternValue('${eventName}', 'fadeOut', this.value)">
                <div class="range-value">${pattern.fadeOut || 0}ms</div>
            </div>
            <div class="form-group">
                <label>Pattern Type</label>
                <select onchange="patternEditor.updatePatternValue('${eventName}', 'pattern', this.value)">
                    ${this.templates.map(t => 
                        `<option value="${t.pattern}" ${pattern.pattern === t.pattern ? 'selected' : ''}>${t.name}</option>`
                    ).join('')}
                </select>
            </div>
        `;
    }

    updatePatternValue(eventName, property, value) {
        if (!this.selectedShip) return;
        
        const pattern = this.currentPattern.ships[this.selectedShip].events[eventName];
        if (property === 'frequency' || property === 'intensity' || property === 'duration' || property === 'fadeIn' || property === 'fadeOut') {
            pattern[property] = parseInt(value);
        } else {
            pattern[property] = value;
        }
        
        this.markDirty();
        this.renderEventEditor(); // Re-render to update displays
    }

    showAddShipModal() {
        document.getElementById('addShipModal').style.display = 'block';
    }

    showAddEventModal() {
        if (!this.selectedShip) {
            this.showError('Please select a ship first');
            return;
        }
        document.getElementById('addEventModal').style.display = 'block';
    }

    confirmAddShip() {
        const shipType = document.getElementById('shipType').value;
        const displayName = document.getElementById('shipDisplayName').value;
        
        if (!shipType) {
            this.showError('Please select a ship type');
            return;
        }

        if (this.currentPattern.ships[shipType]) {
            this.showError('Ship already exists in this pattern pack');
            return;
        }

        // Add ship using the helper method from the controller
        this.currentPattern.ships[shipType] = {
            displayName: displayName || shipType,
            class: this.determineShipClass(shipType),
            role: this.determineShipRole(shipType),
            events: {}
        };

        this.renderShipList();
        this.markDirty();
        this.closeModal('addShipModal');
        
        // Clear form
        document.getElementById('shipType').value = '';
        document.getElementById('shipDisplayName').value = '';
    }

    confirmAddEvent() {
        const eventType = document.getElementById('eventType').value;
        const templateType = document.getElementById('patternTemplate').value;
        
        if (!eventType) {
            this.showError('Please select an event type');
            return;
        }

        if (this.currentPattern.ships[this.selectedShip].events[eventType]) {
            this.showError('Event already exists for this ship');
            return;
        }

        // Create pattern from template
        const template = this.templates.find(t => t.pattern === templateType);
        const pattern = template ? { ...template.defaultSettings } : {
            name: eventType,
            pattern: 'Pulse',
            frequency: 40,
            intensity: 70,
            duration: 500,
            fadeIn: 0,
            fadeOut: 0
        };

        this.currentPattern.ships[this.selectedShip].events[eventType] = pattern;
        
        this.renderEventEditor();
        this.markDirty();
        this.closeModal('addEventModal');
        
        // Clear form
        document.getElementById('eventType').value = '';
        document.getElementById('patternTemplate').value = '';
    }

    removeShip(shipType) {
        if (confirm(`Remove ship "${shipType}" and all its events?`)) {
            delete this.currentPattern.ships[shipType];
            if (this.selectedShip === shipType) {
                this.selectedShip = null;
                document.getElementById('eventEditor').style.display = 'none';
            }
            this.renderShipList();
            this.markDirty();
        }
    }

    removeEvent(eventName) {
        if (confirm(`Remove event "${eventName}"?`)) {
            delete this.currentPattern.ships[this.selectedShip].events[eventName];
            this.renderEventEditor();
            this.markDirty();
        }
    }

    async testPattern(eventName) {
        if (!this.selectedShip) return;
        
        const pattern = this.currentPattern.ships[this.selectedShip].events[eventName];
        
        try {
            const response = await fetch('/api/PatternEditor/test', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pattern })
            });

            if (response.ok) {
                this.showSuccess(`Testing pattern: ${eventName}`);
            } else {
                throw new Error('Failed to test pattern');
            }
        } catch (error) {
            console.error('Failed to test pattern:', error);
            this.showError('Failed to test pattern');
        }
    }

    async testCurrentPattern() {
        if (!this.selectedShip) {
            this.showError('Please select a ship and event first');
            return;
        }

        const events = Object.keys(this.currentPattern.ships[this.selectedShip].events);
        if (events.length === 0) {
            this.showError('No events to test for selected ship');
            return;
        }

        // Test the first event
        await this.testPattern(events[0]);
    }

    updatePatternFromForm() {
        this.currentPattern.metadata.name = document.getElementById('packName').value;
        this.currentPattern.metadata.author = document.getElementById('author').value;
        this.currentPattern.metadata.description = document.getElementById('description').value;
        this.currentPattern.metadata.version = document.getElementById('version').value;
        this.currentPattern.metadata.tags = this.parseTags(document.getElementById('tags').value);
        this.currentPattern.metadata.lastModified = new Date().toISOString();
    }

    validateRequiredFields() {
        const packName = document.getElementById('packName').value.trim();
        const author = document.getElementById('author').value.trim();

        if (!packName) {
            this.showError('Pack name is required');
            document.getElementById('packName').focus();
            return false;
        }

        if (!author) {
            this.showError('Author name is required');
            document.getElementById('author').focus();
            return false;
        }

        return true;
    }

    updateFilenamePreview() {
        const packName = document.getElementById('packName').value || 'PackName';
        const author = document.getElementById('author').value || 'Author';
        
        // Clean names for filename
        const safePack = packName.replace(/[^\w\s\-]/g, '').replace(/\s+/g, '_');
        const safeAuthor = author.replace(/[^\w\s\-]/g, '').replace(/\s+/g, '_');
        
        const preview = `${safeAuthor}_${safePack}_timestamp.json`;
        document.getElementById('filenamePreview').textContent = preview;
    }

    parseTags(tagString) {
        return tagString.split(',').map(tag => tag.trim()).filter(tag => tag.length > 0);
    }

    markDirty() {
        this.isDirty = true;
    }

    closeModal(modalId) {
        document.getElementById(modalId).style.display = 'none';
    }

    showError(message) {
        // You could implement a proper toast/notification system here
        alert('Error: ' + message);
    }

    showSuccess(message) {
        // You could implement a proper toast/notification system here
        console.log('Success:', message);
        // For now, just use a temporary visual indicator
        const validation = document.getElementById('validationPanel');
        const originalContent = validation.innerHTML;
        validation.innerHTML = `<div class="validation-success">✅ ${message}</div>`;
        setTimeout(() => {
            validation.innerHTML = originalContent;
        }, 3000);
    }

    // Helper methods for ship classification (should match server-side logic)
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
}

// Global functions for HTML onclick handlers
function createNewPattern() { patternEditor.createNewPattern(); }
function savePattern() { patternEditor.savePattern(); }
function validatePattern() { patternEditor.validatePattern(); }
function loadUserFiles() { patternEditor.loadUserFiles(); }
function refreshPatternFiles() { patternEditor.refreshPatternFiles(); }
function addShip() { patternEditor.showAddShipModal(); }
function testCurrentPattern() { patternEditor.testCurrentPattern(); }
function closeModal(modalId) { patternEditor.closeModal(modalId); }
function confirmAddShip() { patternEditor.confirmAddShip(); }
function confirmAddEvent() { patternEditor.confirmAddEvent(); }

function importPattern() {
    // Create a file input and trigger it
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.json';
    input.onchange = async (e) => {
        const file = e.target.files[0];
        if (file) {
            try {
                const text = await file.text();
                const pattern = JSON.parse(text);
                patternEditor.currentPattern = pattern;
                patternEditor.updateUI();
                patternEditor.markDirty();
                patternEditor.showSuccess(`Imported pattern: ${pattern.metadata.name}`);
            } catch (error) {
                patternEditor.showError('Failed to import pattern file: ' + error.message);
            }
        }
    };
    input.click();
}

function exportPattern() {
    if (!patternEditor.currentPattern) {
        patternEditor.showError('No pattern to export');
        return;
    }
    
    patternEditor.updatePatternFromForm();
    
    const dataStr = JSON.stringify(patternEditor.currentPattern, null, 2);
    const dataBlob = new Blob([dataStr], { type: 'application/json' });
    
    const link = document.createElement('a');
    link.href = URL.createObjectURL(dataBlob);
    
    const packName = patternEditor.currentPattern.metadata.name.replace(/[^\w\s\-]/g, '').replace(/\s+/g, '_');
    const author = patternEditor.currentPattern.metadata.author.replace(/[^\w\s\-]/g, '').replace(/\s+/g, '_');
    link.download = `${author}_${packName}.json`;
    
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    
    patternEditor.showSuccess('Pattern exported successfully');
}

// Initialize the pattern editor when page loads
let patternEditor;
document.addEventListener('DOMContentLoaded', () => {
    patternEditor = new PatternEditor();
});

// Close modals when clicking outside
window.onclick = function(event) {
    const modals = document.querySelectorAll('.modal');
    modals.forEach(modal => {
        if (event.target === modal) {
            modal.style.display = 'none';
        }
    });
}