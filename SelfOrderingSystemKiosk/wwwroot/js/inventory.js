const inventoryPageConfig = window.inventoryPageConfig ?? {};
        const deliveryImportIngredients = inventoryPageConfig.deliveryImportIngredients || [];
        let deliveryImportToken = '';
        let deliveryImportPoll = null;

        document.addEventListener('DOMContentLoaded', function() {
            const shouldAutoPrint = Boolean(inventoryPageConfig.shouldAutoPrint);
            if (shouldAutoPrint) {
                setTimeout(() => window.print(), 250);
            }

            // Search functionality
            const searchInput = document.getElementById('searchInput');
            if (searchInput) {
                searchInput.addEventListener('keyup', function() {
                    const searchTerm = this.value.trim().toLowerCase();
                    const tableBody = document.getElementById('inventoryTableBody');
                    if (!tableBody) return;
                    
                    const rows = tableBody.querySelectorAll('tr');
                    let visibleCount = 0;
                    
                    rows.forEach(row => {
                        const rowText = row.textContent.trim().toLowerCase();
                        
                        if (searchTerm === '' || rowText.includes(searchTerm)) {
                            row.style.display = '';
                            visibleCount++;
                        } else {
                            row.style.display = 'none';
                        }
                    });
                    
                    // Show "no results" message if needed
                    if (visibleCount === 0 && searchTerm !== '') {
                        if (!document.getElementById('noSearchResults')) {
                            const noResults = document.createElement('tr');
                            noResults.id = 'noSearchResults';
                            noResults.innerHTML = '<td colspan="10" style="text-align: center; padding: 20px; color: #999;">No ingredients match your search.</td>';
                            tableBody.appendChild(noResults);
                        }
                    } else {
                        const noResults = document.getElementById('noSearchResults');
                        if (noResults) noResults.remove();
                    }
                });
            }
        });

        function confirmAction(message) {
            const btn = document.getElementById('addIngredientBtn');
            if (btn.disabled) return; // Prevent double submit
            
            const itemInputs = Array.from(document.querySelectorAll('#addIngredientForm input[name="item"]'));
            const itemNames = itemInputs.map(x => x.value.trim()).filter(Boolean);
            if (itemNames.length === 0) {
                alert('Please enter at least one ingredient name.');
                return;
            }

            const repeatedName = itemNames.find((name, index) =>
                itemNames.findIndex(other => other.toLowerCase() === name.toLowerCase()) !== index);
            if (repeatedName) {
                alert(`Ingredient '${repeatedName}' is listed more than once.`);
                return;
            }

            const duplicateChecks = itemNames.map(itemName =>
                fetch(`/Admin/Inventory/CheckDuplicate?itemName=${encodeURIComponent(itemName)}`)
                    .then(response => response.json())
                    .then(data => ({ itemName, exists: data.exists })));

            Promise.all(duplicateChecks)
                .then(results => {
                    const duplicate = results.find(x => x.exists);
                    if (duplicate) {
                        alert(`An ingredient with the name '${duplicate.itemName}' already exists.`);
                        return;
                    }
                    if (confirm(message)) {
                        btn.disabled = true;
                        btn.textContent = 'Adding...';
                        document.getElementById('addIngredientForm').submit();
                    }
                })
                .catch(error => {
                    console.error('Error checking duplicate:', error);
                    alert('Error checking for duplicates. Please try again.');
                });
        }

        document.addEventListener('DOMContentLoaded', function() {
            updateIngredientRemoveButtons();

            document.getElementById('addIngredientBtn').addEventListener('click', function(e) {
                e.preventDefault();
                confirmAction('Add these ingredients?');
            });

            // Close edit modal after form submission (like menu)
            const editForm = document.getElementById('invEditForm');
            if (editForm) {
                editForm.addEventListener('submit', function() {
                    setTimeout(function() {
                        closeInvEditModal();
                    }, 100);
                });
            }
        });

        function openInvEditModal(btn) {
            const modal = document.getElementById('invEditModal');
            if (!modal) return;
            document.getElementById('invEditId').value = btn.getAttribute('data-id') || '';
            document.getElementById('invEditItem').value = btn.getAttribute('data-item') || '';
            document.getElementById('invEditCategory').value = btn.getAttribute('data-category') || '';
            document.getElementById('invEditUnit').value = btn.getAttribute('data-unit') || 'g';
            document.getElementById('invEditCost').value = btn.getAttribute('data-cost') || '0';
            document.getElementById('invEditExpiration').value = btn.getAttribute('data-expiration') || '';
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');
        }

        function openAddIngredientModal() {
            const modal = document.getElementById('addIngredientModal');
            const form = document.getElementById('addIngredientForm');
            const btn = document.getElementById('addIngredientBtn');
            if (!modal) return;
            if (form) {
                form.reset();
                resetIngredientRows();
            }
            if (btn) {
                btn.disabled = false;
                btn.textContent = 'Add ingredients';
            }
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');
        }

        function createIngredientRow() {
            const template = document.querySelector('#ingredientRows .ingredient-add-row');
            const row = template.cloneNode(true);
            row.querySelectorAll('input').forEach(input => {
                input.value = input.name === 'reorderLevel' ? '10' : input.name === 'stock' || input.name === 'costPerUnit' ? '0' : '';
            });
            row.querySelectorAll('select').forEach(select => {
                select.selectedIndex = select.name === 'unit' ? 0 : 0;
            });
            return row;
        }

        function addIngredientRow() {
            const rows = document.getElementById('ingredientRows');
            if (!rows) return;
            rows.appendChild(createIngredientRow());
            updateIngredientRemoveButtons();
        }

        function removeIngredientRow(button) {
            const rows = document.getElementById('ingredientRows');
            const row = button.closest('.ingredient-add-row');
            if (!rows || !row || rows.querySelectorAll('.ingredient-add-row').length <= 1) return;
            row.remove();
            updateIngredientRemoveButtons();
        }

        function resetIngredientRows() {
            const rows = document.getElementById('ingredientRows');
            if (!rows) return;
            const first = rows.querySelector('.ingredient-add-row');
            rows.innerHTML = '';
            rows.appendChild(first);
            updateIngredientRemoveButtons();
        }

        function updateIngredientRemoveButtons() {
            const rowCount = document.querySelectorAll('#ingredientRows .ingredient-add-row').length;
            document.querySelectorAll('#ingredientRows .ingredient-remove-row').forEach(btn => {
                btn.disabled = rowCount <= 1;
                btn.style.opacity = rowCount <= 1 ? '0.45' : '1';
                btn.style.cursor = rowCount <= 1 ? 'not-allowed' : 'pointer';
            });
        }

        function closeAddIngredientModal() {
            const modal = document.getElementById('addIngredientModal');
            if (!modal) return;
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }

        function closeInvEditModal() {
            const modal = document.getElementById('invEditModal');
            if (!modal) return;
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }

        document.addEventListener('click', function (e) {
            const b = e.target.closest('.inv-edit-btn');
            if (b) {
                e.preventDefault();
                openInvEditModal(b);
            }
        });

        document.addEventListener('click', function (e) {
            const b = e.target.closest('.restock-btn');
            if (b) {
                e.preventDefault();
                openRestockModal(b);
            }
        });

        document.getElementById('invEditModal')?.addEventListener('click', function (e) {
            if (e.target === this) closeInvEditModal();
        });

        document.getElementById('restockModal')?.addEventListener('click', function (e) {
            if (e.target === this) closeRestockModal();
        });

        document.getElementById('addIngredientModal')?.addEventListener('click', function (e) {
            if (e.target === this) closeAddIngredientModal();
        });

        document.getElementById('deliveryImportModal')?.addEventListener('click', function (e) {
            if (e.target === this) closeDeliveryImportModal();
        });

        document.getElementById('deliveryReviewModal')?.addEventListener('click', function (e) {
            if (e.target === this) closeDeliveryReviewModal();
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                closeRestockModal();
                closeInvEditModal();
                closeAddIngredientModal();
                closeDeliveryImportModal();
                closeDeliveryReviewModal();
            }
        });

        function getAntiForgeryToken() {
            return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        }

        function startDeliveryImport() {
            const modal = document.getElementById('deliveryImportModal');
            const status = document.getElementById('deliveryImportStatus');
            const qr = document.getElementById('deliveryImportQr');
            const link = document.getElementById('deliveryImportLink');
            if (!modal || !status || !qr || !link) return;

            status.textContent = 'Creating phone scan session...';
            qr.removeAttribute('src');
            link.href = '#';
            deliveryImportToken = '';
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');

            const body = new URLSearchParams();
            body.set('branchFilter', document.getElementById('branchFilter')?.value || inventoryPageConfig.branchFilter || '');

            fetch('/Admin/Inventory/StartDeliveryImport', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'RequestVerificationToken': getAntiForgeryToken()
                },
                body
            })
            .then(r => r.json())
            .then(data => {
                if (!data.success) {
                    status.textContent = data.message || 'Unable to start phone scan.';
                    return;
                }

                deliveryImportToken = data.token;
                qr.src = data.qrDataUrl;
                link.href = data.scanUrl;
                status.textContent = `Waiting for phone upload. Link expires at ${data.expiresAt}.`;
                startDeliveryImportPolling();
            })
            .catch(error => {
                console.error(error);
                status.textContent = 'Unable to start phone scan.';
            });
        }

        function startDeliveryImportPolling() {
            if (deliveryImportPoll) clearInterval(deliveryImportPoll);
            deliveryImportPoll = setInterval(checkDeliveryImportStatus, 2500);
            checkDeliveryImportStatus();
        }

        function checkDeliveryImportStatus() {
            if (!deliveryImportToken) return;
            fetch(`/Admin/Inventory/DeliveryImportStatus?token=${encodeURIComponent(deliveryImportToken)}`)
                .then(r => r.json())
                .then(data => {
                    const status = document.getElementById('deliveryImportStatus');
                    if (!data.success) {
                        if (status) status.textContent = data.message || 'Waiting for phone upload...';
                        return;
                    }

                    if (status) status.textContent = data.uploaded
                        ? `Upload received at ${data.uploadedAt || 'the phone'}. Parsed ${data.rows?.length || 0} row(s).`
                        : `Status: ${data.status}. Waiting for phone upload until ${data.expiresAt || 'expiry'}...`;

                    if (data.uploaded) {
                        clearInterval(deliveryImportPoll);
                        deliveryImportPoll = null;
                        openDeliveryReviewModal(data.rows || []);
                    }
                })
                .catch(error => console.error('Delivery import status failed:', error));
        }

        function openDeliveryReviewModal(rows) {
            closeDeliveryImportModal(false);
            const modal = document.getElementById('deliveryReviewModal');
            const tbody = document.getElementById('deliveryReviewRows');
            if (!modal || !tbody) return;

            tbody.innerHTML = '';
            if (!rows.length) {
                tbody.innerHTML = '';
                addDeliveryReviewRow();
            }

            rows.forEach(row => tbody.appendChild(createDeliveryReviewRow(row)));
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');
        }

        function openManualDeliveryImport() {
            if (!deliveryImportToken) {
                alert('Start a delivery import session first.');
                return;
            }

            if (deliveryImportPoll) {
                clearInterval(deliveryImportPoll);
                deliveryImportPoll = null;
            }

            openDeliveryReviewModal([]);
        }

        function addDeliveryReviewRow() {
            const tbody = document.getElementById('deliveryReviewRows');
            if (!tbody) return;
            tbody.appendChild(createDeliveryReviewRow({
                itemName: '',
                quantity: 0,
                unit: '',
                matchedIngredientId: '',
                confidence: 0
            }));
        }

        function createDeliveryReviewRow(row) {
            const tr = document.createElement('tr');
            const confidence = Number(row.confidence || 0);
            const optionHtml = ['<option value="">Ignore row</option>']
                .concat(deliveryImportIngredients.map(item => {
                    const selected = item.id === row.matchedIngredientId ? ' selected' : '';
                    return `<option value="${escapeHtml(item.id)}" data-unit="${escapeHtml(item.unit)}"${selected}>${escapeHtml(item.name)}</option>`;
                }))
                .join('');
            if (!row.matchedIngredientId || confidence < 75) {
                tr.classList.add('needs-review');
            }

            tr.innerHTML = `
                <td><input type="text" class="delivery-ocr-name" value="${escapeHtml(row.itemName || '')}" /></td>
                <td><select class="delivery-ingredient">${optionHtml}</select></td>
                <td><input type="number" class="delivery-qty" value="${Number(row.quantity || 0)}" min="0" /></td>
                <td><input type="text" class="delivery-unit" value="${escapeHtml(row.unit || '')}" /></td>
                <td><span class="confidence-badge">${confidence}%</span></td>
                <td><input type="text" class="delivery-note" placeholder="Optional" /></td>
                <td><button type="button" class="delivery-row-remove" onclick="removeDeliveryReviewRow(this)">Remove</button></td>
            `;
            tr.querySelector('.delivery-ingredient')?.addEventListener('change', function() {
                const selected = this.options[this.selectedIndex];
                const unit = selected?.getAttribute('data-unit') || '';
                const unitInput = tr.querySelector('.delivery-unit');
                if (unitInput && !unitInput.value.trim()) {
                    unitInput.value = unit;
                }
                tr.classList.toggle('needs-review', !this.value || confidence < 75);
            });
            return tr;
        }

        function removeDeliveryReviewRow(button) {
            const row = button.closest('tr');
            if (row) row.remove();
            if (!document.querySelector('#deliveryReviewRows tr')) {
                addDeliveryReviewRow();
            }
        }

        function confirmDeliveryImport() {
            if (!deliveryImportToken) {
                alert('No active import session.');
                return;
            }

            const rows = Array.from(document.querySelectorAll('#deliveryReviewRows tr')).map(tr => ({
                ingredientId: tr.querySelector('.delivery-ingredient')?.value || '',
                quantity: Number(tr.querySelector('.delivery-qty')?.value || 0),
                note: tr.querySelector('.delivery-note')?.value || ''
            })).filter(row => row.ingredientId && row.quantity > 0);

            if (!rows.length) {
                alert('Choose at least one ingredient and quantity to import.');
                return;
            }

            const ignoredRows = Array.from(document.querySelectorAll('#deliveryReviewRows tr'))
                .filter(tr => !tr.querySelector('.delivery-ingredient')?.value && Number(tr.querySelector('.delivery-qty')?.value || 0) > 0);
            if (ignoredRows.length > 0 && !confirm(`${ignoredRows.length} row(s) have an amount but no matched ingredient. Ignore them and continue?`)) {
                return;
            }

            fetch('/Admin/Inventory/ConfirmDeliveryImport', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': getAntiForgeryToken()
                },
                body: JSON.stringify({ token: deliveryImportToken, rows })
            })
            .then(r => r.json())
            .then(data => {
                if (!data.success) {
                    alert(data.message || 'Unable to confirm import.');
                    return;
                }
                alert(data.message || 'Delivery import confirmed.');
                window.location.reload();
            })
            .catch(error => {
                console.error(error);
                alert('Unable to confirm import.');
            });
        }

        function closeDeliveryImportModal(stopPolling = true) {
            const modal = document.getElementById('deliveryImportModal');
            if (!modal) return;
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
            if (stopPolling && deliveryImportPoll) {
                clearInterval(deliveryImportPoll);
                deliveryImportPoll = null;
            }
        }

        function closeDeliveryReviewModal() {
            const modal = document.getElementById('deliveryReviewModal');
            if (!modal) return;
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }

        function escapeHtml(value) {
            return String(value ?? '')
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#39;');
        }

        // Update total stock when restock amount changes
        document.addEventListener('input', function (e) {
            if (e.target.id === 'restockAmount') {
                updateTotalStock();
            }
        });

        // Restock modal functions
        function openRestockModal(btn) {
            const modal = document.getElementById('restockModal');
            if (!modal) return;
            
            const currentStock = parseInt(btn.getAttribute('data-current') || 0);
            const reorderLevel = parseInt(btn.getAttribute('data-reorder') || 10);
            const unit = btn.getAttribute('data-unit') || 'g';
            const id = btn.getAttribute('data-id');
            
            document.getElementById('currentStockDisplay').textContent = `${currentStock} ${unit}`;
            document.getElementById('unitDisplay').textContent = unit;
            document.getElementById('restockAmount').value = reorderLevel;
            document.getElementById('batchNote').value = '';
            updateTotalStock();
            
            // Store the item ID for later use
            modal.setAttribute('data-item-id', id);
            
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');
        }

        function closeRestockModal() {
            const modal = document.getElementById('restockModal');
            if (!modal) return;
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }

        function updateTotalStock() {
            const currentStock = parseInt(document.getElementById('currentStockDisplay').textContent.split(' ')[0] || 0);
            const restockAmount = parseInt(document.getElementById('restockAmount').value || 0);
            const total = currentStock + restockAmount;
            document.getElementById('totalStockDisplay').textContent = `${total} ${document.getElementById('unitDisplay').textContent}`;
        }

        function confirmRestock() {
            const modal = document.getElementById('restockModal');
            const itemId = modal.getAttribute('data-item-id');
            const restockAmount = parseInt(document.getElementById('restockAmount').value || 0);
            const batchNote = document.getElementById('batchNote').value || '';
            
            if (restockAmount <= 0) {
                alert('Please enter a valid restock amount.');
                return;
            }
            
            // Create and submit form
            const form = document.createElement('form');
            form.method = 'post';
            form.action = '/Admin/Inventory/Restock';
            form.style.display = 'none';

            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
            if (token) {
                const tokenInput = document.createElement('input');
                tokenInput.type = 'hidden';
                tokenInput.name = '__RequestVerificationToken';
                tokenInput.value = token;
                form.appendChild(tokenInput);
            }
            
            const idInput = document.createElement('input');
            idInput.type = 'hidden';
            idInput.name = 'id';
            idInput.value = itemId;
            
            const amountInput = document.createElement('input');
            amountInput.type = 'hidden';
            amountInput.name = 'amount';
            amountInput.value = restockAmount;

            const batchInput = document.createElement('input');
            batchInput.type = 'hidden';
            batchInput.name = 'batchNote';
            batchInput.value = batchNote;

            const branchInput = document.createElement('input');
            branchInput.type = 'hidden';
            branchInput.name = 'branchFilter';
            branchInput.value = document.getElementById('branchFilter')?.value || inventoryPageConfig.branchFilter || '';

            const actionInput = document.createElement('input');
            actionInput.type = 'hidden';
            actionInput.name = 'actionView';
            actionInput.value = inventoryPageConfig.actionView || "all";
            
            form.appendChild(idInput);
            form.appendChild(amountInput);
            form.appendChild(batchInput);
            form.appendChild(branchInput);
            form.appendChild(actionInput);
            document.body.appendChild(form);
            
            closeRestockModal();
            form.submit();
        }

        document.addEventListener('DOMContentLoaded', function () {
            const itemInput = document.getElementById('invEditItem');
            const categorySelect = document.getElementById('invEditCategory');
            
            if (itemInput && categorySelect) {
                itemInput.addEventListener('input', function() {
                    const itemName = this.value.trim();
                    if (itemName.length > 0) {
                        fetch(`/Admin/Inventory/GetCategory?itemName=${encodeURIComponent(itemName)}`)
                            .then(response => response.json())
                            .then(data => {
                                if (data.category) {
                                    categorySelect.value = data.category;
                                }
                            })
                            .catch(error => console.error('Error fetching category:', error));
                    }
                });
            }
        });

        let currentSortColumn = -1;
        let currentSortDirection = 'asc';

        function sortTable(columnIndex, columnType) {
            const table = document.getElementById('inventoryTable');
            if (!table) return;
            const tbody = document.getElementById('inventoryTableBody');
            if (!tbody) return;
            const rows = Array.from(tbody.querySelectorAll('tr'));
            if (rows.length === 0) return;
            const headers = table.querySelectorAll('thead th');

            headers.forEach(header => {
                const icon = header.querySelector('.sort-icon');
                if (icon) {
                    icon.textContent = '⇅';
                    icon.style.color = '';
                }
            });

            if (currentSortColumn === columnIndex) {
                currentSortDirection = currentSortDirection === 'asc' ? 'desc' : 'asc';
            } else {
                currentSortDirection = 'asc';
                currentSortColumn = columnIndex;
            }

            const currentHeader = headers[columnIndex];
            if (currentHeader) {
                const currentIcon = currentHeader.querySelector('.sort-icon');
                if (currentIcon) {
                    currentIcon.textContent = currentSortDirection === 'asc' ? '↑' : '↓';
                    currentIcon.style.color = '#d35400';
                }
            }

            rows.sort((a, b) => {
                const aCell = a.cells[columnIndex];
                const bCell = b.cells[columnIndex];
                if (!aCell || !bCell) return 0;
                const aValue = aCell.getAttribute('data-sort-value') || aCell.textContent.trim();
                const bValue = bCell.getAttribute('data-sort-value') || bCell.textContent.trim();
                let comparison = 0;
                if (columnType === 'stock' || columnType === 'reorder') {
                    const aNum = parseFloat(aValue) || 0;
                    const bNum = parseFloat(bValue) || 0;
                    comparison = aNum - bNum;
                } else {
                    comparison = aValue.localeCompare(bValue, undefined, { sensitivity: 'base' });
                }
                return currentSortDirection === 'asc' ? comparison : -comparison;
            });

            rows.forEach(row => {
                if (row.parentNode === tbody) tbody.removeChild(row);
            });
            rows.forEach(row => tbody.appendChild(row));
        }

        function filterByBranch() {
            const branchId = document.getElementById('branchFilter').value;
            const url = new URL(window.location.href);
            url.searchParams.set('branchFilter', branchId);
            window.location.href = url.toString();
        }

        function applyExpiryUntil(input) {
            const expiryFilter = document.getElementById('expiryFilter');
            if (input.value && expiryFilter) {
                expiryFilter.value = 'custom';
            }
            input.form.submit();
        }

        function printAllSupplies() {
            const url = new URL(window.location.href);
            url.searchParams.set('categoryFilter', 'all');
            url.searchParams.set('expiryFilter', 'all');
            url.searchParams.set('actionView', 'all');
            url.searchParams.delete('expiryUntil');
            url.searchParams.set('print', 'true');
            window.location.href = url.toString();
        }

