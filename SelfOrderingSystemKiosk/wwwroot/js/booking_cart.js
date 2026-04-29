// =====================
// 🧾 Booking & Cart Logic
// =====================

const pricePerHead = 477;
let personCount = 0;
let cart = [];

function readPositiveCount(value) {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

// Initialize personCount from reorder if available (set early to avoid timing issues)
if (typeof window !== 'undefined' && window.isReorder === true) {
    personCount = readPositiveCount(window.reorderPersonCount);
}
if (personCount === 0 && typeof window !== 'undefined') {
    personCount = readPositiveCount(window.orderingSessionPersonCount);
}

// 🌐 Modal control (global functions)
function openModal(id) {
    const modal = document.getElementById(id);
    if (modal) modal.classList.add("active");
}

function closeModal(id) {
    const modal = document.getElementById(id);
    if (modal) {
        modal.classList.remove("active");
        modal.style.animation = "fadeOut 0.25s ease-in-out";
        setTimeout(() => {
            modal.style.animation = "";
        }, 250);
    }
}

// 🌟 Notification system
function showNotification(message, type = 'info', duration = 3000) {
    const container = document.getElementById('notificationContainer');
    if (!container) return;

    const notif = document.createElement('div');
    notif.textContent = message;

    notif.style.padding = '10px 15px';
    notif.style.borderRadius = '8px';
    notif.style.color = '#fff';
    notif.style.minWidth = '200px';
    notif.style.fontSize = '0.9rem';
    notif.style.boxShadow = '0 4px 12px rgba(0,0,0,0.15)';
    notif.style.opacity = '0';
    notif.style.transition = 'opacity 0.3s, transform 0.3s';
    notif.style.transform = 'translateY(-20px)';

    if (type === 'error') notif.style.backgroundColor = '#e74c3c';
    else if (type === 'success') notif.style.backgroundColor = '#28a745';
    else notif.style.backgroundColor = '#3498db';

    container.appendChild(notif);

    requestAnimationFrame(() => {
        notif.style.opacity = '1';
        notif.style.transform = 'translateY(0)';
    });

    setTimeout(() => {
        notif.style.opacity = '0';
        notif.style.transform = 'translateY(-20px)';
        setTimeout(() => container.removeChild(notif), 300);
    }, duration);
}

// ==========================
// 📏 Flavor & Quantity Limits
// ==========================
function getFlavorLimit() {
    if (personCount <= 2) return 4;
    if (personCount <= 6) return 8;
    return Infinity; // unlimited
}

function getQuantityLimit() {
    if (personCount <= 2) return 4;
    if (personCount <= 6) return 8;
    return 12;
}

function refreshRememberedPersonCount() {
    if (typeof window === 'undefined') return;

    if (window.isReorder === true) {
        personCount = readPositiveCount(window.reorderPersonCount) || personCount;
    }

    personCount = personCount || readPositiveCount(window.orderingSessionPersonCount);
}

function displayPersonCount() {
    const personCountDisplay = document.querySelector(".person-count");
    if (personCountDisplay && personCount > 0) {
        personCountDisplay.textContent = `${personCount} Person${personCount > 1 ? "s" : ""}`;
    }

    updateOrderSummary();
}

function savePersonCount() {
    return fetch(`/Customer/Kiosk/SaveOrderingSession?personCount=${personCount}`, {
        method: 'POST'
    }).catch(err => console.error('Error saving ordering session:', err));
}

function refreshVariantRow(row) {
    const select = row?.querySelector('.variant-select');
    const servingSelect = row?.querySelector('.serving-select');
    const button = row?.querySelector('.add-to-cart');
    const imageEl = row?.querySelector('img');
    if (!select || !button) return;

    const option = select.selectedOptions[0];
    const image = option?.getAttribute('data-image') || '';
    button.setAttribute('data-name', option.value);
    if (image) {
        button.setAttribute('data-image', image);
        if (imageEl) imageEl.src = image;
    }
    if (servingSelect) {
        button.setAttribute('data-serving-size', servingSelect.value);
    }
}

function cartKey(item) {
    return [
        item.name,
        item.variantLabel || '',
        item.servingSize || ''
    ].join('::');
}

// ✅ Confirm persons
document.addEventListener("DOMContentLoaded", () => {
    refreshRememberedPersonCount();
    displayPersonCount();

    if (personCount > 0) {
        closeModal("personModal");
        closeModal("rulesModal");
    } else {
        openModal("personModal");
    }

    const confirmBtn = document.getElementById("confirmPersons");
    if (confirmBtn) {
        confirmBtn.addEventListener("click", function () {
            const input = document.getElementById("personCount").value;
            if (input < 1 || input === "") {
                showNotification("Please enter a valid number of persons.", 'error');
                return;
            }

            personCount = parseInt(input);
            document.querySelector(".person-count").textContent =
                `${personCount} Person${personCount > 1 ? "s" : ""}`;

            savePersonCount();
            
            // Update order summary with a small delay to ensure DOM is ready
            setTimeout(() => {
                updateOrderSummary();
            }, 100);

            closeModal("personModal");
            showRulesModal();
        });
    }

    const addPersonBtn = document.getElementById("addPersonBtn");
    if (addPersonBtn) {
        addPersonBtn.addEventListener("click", function () {
            const input = prompt("How many persons should be added?", "1");
            const added = readPositiveCount(input);
            if (added <= 0) {
                showNotification("Please enter a valid number of persons.", "error");
                return;
            }

            personCount += added;
            displayPersonCount();
            savePersonCount();
            showNotification(`${added} person${added > 1 ? "s" : ""} added.`, "success");
        });
    }

    document.querySelectorAll('.variant-select').forEach(select => {
        select.addEventListener('change', function () {
            refreshVariantRow(this.closest('.menu-row'));
        });
    });

    document.querySelectorAll('.serving-select').forEach(select => {
        select.addEventListener('change', function () {
            const row = this.closest('.menu-row');
            const button = row?.querySelector('.add-to-cart');
            if (button) button.setAttribute('data-serving-size', this.value);
        });
    });

    // 🍗 Add to cart by two’s
    document.querySelectorAll('.add-to-cart').forEach(button => {
        button.addEventListener('click', function (e) {
            e.preventDefault();

            if (personCount === 0) {
                showNotification("Please enter the number of persons first.", 'error');
                openModal("personModal");
                return;
            }

            const name = this.getAttribute('data-name');
            const image = this.getAttribute('data-image');
            const isWingFlavor = this.getAttribute('data-is-wing-flavor') !== 'false';
            const variants = JSON.parse(this.getAttribute('data-variants') || '[]');
            const selectedVariant = variants.find(v => v.name === name);
            const servingSize = this.getAttribute('data-serving-size') || '';
            const flavorLimit = getFlavorLimit();

            const selectedWingFlavorCount = cart.filter(i => i.isWingFlavor !== false).length;
            if (isWingFlavor && selectedWingFlavorCount >= flavorLimit && !cart.find(i => i.name === name && i.isWingFlavor !== false)) {
                showNotification(`You can only choose up to ${flavorLimit} flavors.`, 'error');
                return;
            }

            const newItem = {
                name,
                image,
                quantity: isWingFlavor ? Math.min(4, getQuantityLimit()) : 1,
                isWingFlavor,
                variants,
                variantLabel: selectedVariant?.label || '',
                servingSize
            };
            const existingItem = cart.find(item => cartKey(item) === cartKey(newItem));
            const quantityLimit = getQuantityLimit();

            if (existingItem) {
                if (isWingFlavor && existingItem.quantity >= quantityLimit) {
                    showNotification(`You can only order up to ${quantityLimit} pcs per flavor.`, 'error');
                    return;
                }
                existingItem.quantity += isWingFlavor ? Math.min(4, quantityLimit - existingItem.quantity) : 1;
            } else {
                cart.push(newItem);
            }

            updateCartDisplay();

            // ✅ Feedback animation
            this.textContent = 'Added!';
            this.style.backgroundColor = '#28a745';
            setTimeout(() => {
                this.textContent = 'Add to cart';
                this.style.backgroundColor = '';
            }, 500);
        });
    });

    const wingFlavorScreen = document.getElementById('unliWingFlavorScreen');
    const wingFlavorImage = document.getElementById('unliWingFlavorImage');
    const wingFlavorInstruction = document.getElementById('unliWingFlavorInstruction');
    const wingFlavorCount = document.getElementById('unliWingFlavorCount');
    const wingFlavorLimitText = document.getElementById('unliWingFlavorLimitText');
    const wingFlavorAdd = document.getElementById('unliWingFlavorAdd');

    function selectedWingFlavorCards() {
        return Array.from(document.querySelectorAll('#unliWingFlavorGrid .wing-flavor-card.selected'));
    }

    function updateWingFlavorScreenState() {
        if (!wingFlavorScreen) return;

        const selected = selectedWingFlavorCards();
        const limit = getFlavorLimit();
        const atLimit = Number.isFinite(limit) && selected.length >= limit;

        document.querySelectorAll('#unliWingFlavorGrid .wing-flavor-card').forEach(button => {
            button.disabled = atLimit && !button.classList.contains('selected');
        });

        const limitLabel = Number.isFinite(limit) ? limit : 'unlimited';
        wingFlavorCount.textContent = selected.length === 0
            ? '0 selected'
            : `${selected.length}/${limitLabel} selected`;
        wingFlavorLimitText.textContent = selected.length === 0
            ? `Choose up to ${limitLabel} flavor${limit === 1 ? '' : 's'}`
            : selected.map(button => button.getAttribute('data-flavor')).join(', ');
        wingFlavorAdd.disabled = selected.length === 0;
    }

    function openWingFlavorScreen(button) {
        if (personCount === 0) {
            showNotification("Please enter the number of persons first.", 'error');
            openModal("personModal");
            return;
        }

        const image = button.getAttribute('data-image') || '';
        const flavorLimit = getFlavorLimit();
        const quantityLimit = getQuantityLimit();
        const existingFlavors = new Set(cart.filter(item => item.isWingFlavor !== false).map(item => item.name));

        document.querySelectorAll('#unliWingFlavorGrid .wing-flavor-card').forEach(card => {
            const flavor = card.getAttribute('data-flavor');
            card.classList.toggle('selected', existingFlavors.has(flavor));
            card.disabled = false;
        });

        if (wingFlavorImage) wingFlavorImage.src = image;
        if (wingFlavorInstruction) {
            const limitText = Number.isFinite(flavorLimit) ? `up to ${flavorLimit}` : 'unlimited';
            wingFlavorInstruction.textContent = `Choose ${limitText} flavor${flavorLimit === 1 ? '' : 's'}; up to ${quantityLimit} pcs per flavor.`;
        }

        wingFlavorScreen.classList.add('active');
        wingFlavorScreen.setAttribute('aria-hidden', 'false');
        document.body.style.overflow = 'hidden';
        updateWingFlavorScreenState();
    }

    function closeWingFlavorScreen() {
        if (!wingFlavorScreen) return;
        wingFlavorScreen.classList.remove('active');
        wingFlavorScreen.setAttribute('aria-hidden', 'true');
        document.body.style.overflow = '';
    }

    document.querySelectorAll('.unli-wing-flavor-launch').forEach(button => {
        button.addEventListener('click', function () {
            openWingFlavorScreen(this);
        });
    });

    document.querySelectorAll('#unliWingFlavorGrid .wing-flavor-card').forEach(button => {
        button.addEventListener('click', function () {
            if (this.disabled) return;
            this.classList.toggle('selected');
            updateWingFlavorScreenState();
        });
    });

    document.getElementById('unliWingFlavorBack')?.addEventListener('click', closeWingFlavorScreen);
    wingFlavorScreen?.addEventListener('click', function (e) {
        if (e.target === wingFlavorScreen) closeWingFlavorScreen();
    });

    wingFlavorAdd?.addEventListener('click', function () {
        const selected = selectedWingFlavorCards();
        if (selected.length === 0) {
            showNotification('Please choose at least 1 wing flavor.', 'error');
            return;
        }

        const selectedNames = new Set(selected.map(button => button.getAttribute('data-flavor')));
        cart = cart.filter(item => item.isWingFlavor === false || selectedNames.has(item.name));

        selected.forEach(button => {
            const name = button.getAttribute('data-flavor');
            const image = button.getAttribute('data-image');
            const existing = cart.find(item => item.name === name && item.isWingFlavor !== false);
            if (!existing) {
                cart.push({
                    name,
                    image,
                    quantity: Math.min(4, getQuantityLimit()),
                    isWingFlavor: true,
                    variants: [],
                    variantLabel: '',
                    servingSize: ''
                });
            }
        });

        updateCartDisplay();
        closeWingFlavorScreen();
        showNotification('Wing flavors updated.', 'success');
    });
});

// 📋 Show the rules modal dynamically
function showRulesModal() {
    const rulesBody = document.getElementById("rulesBody");

    let message = "";
    if (personCount <= 2) {
        message = `
        <b>For 1–2 Customers:</b><br>
        • You can choose up to <b>4 flavors</b>.<br>
        • Maximum of <b>4 pcs</b> per flavor.<br>
        • Serving time is approximately <b>8 - 12 minutes</b>.<br>
        • Rice, red iced tea, gravy, nachos, potato thins, regular pasta, coffee, and tea are included.
        `;
    } else if (personCount <= 6) {
        message = `
        <b>For 3–6 Customers:</b><br>
        • You can choose up to <b>8 flavors</b>.<br>
        • Maximum of <b>8 pcs</b> per flavor.<br>
        • Serving time is approximately <b>8 - 12 minutes</b>.<br>
        • Rice, red iced tea, gravy, nachos, potato thins, regular pasta, coffee, and tea are included.
        `;
    } else {
        message = `
        <b>For 7+ Customers:</b><br>
        • You can choose <b>unlimited flavors</b>.<br>
        • Up to <b>12 pcs</b> per flavor.<br>
        • Serving time is approximately <b>8 - 12 minutes</b>.<br>
        • Rice, red iced tea, gravy, nachos, potato thins, regular pasta, coffee, and tea are included.
        `;
    }

    rulesBody.innerHTML = message;
    openModal("rulesModal");
}

// 💰 Update order summary breakdown
function updateOrderSummary() {
    const personCountDisplay = document.getElementById('person-count-display');
    const perPersonSubtotalEl = document.getElementById('per-person-subtotal');
    const subtotalAmountEl = document.getElementById('subtotal-amount');
    const taxAmountEl = document.getElementById('tax-amount');
    const orderTotalEl = document.querySelector('.order-total');
    
    // Check if elements exist (for Unlimited menu only)
    if (!personCountDisplay || !perPersonSubtotalEl || !subtotalAmountEl || !orderTotalEl) {
        return; // Not on Unlimited menu page
    }
    
    // Always use the current personCount value
    const currentPersonCount = personCount || 0;
    
    if (currentPersonCount === 0) {
        personCountDisplay.textContent = '0';
        perPersonSubtotalEl.textContent = '₱0.00';
        subtotalAmountEl.innerHTML = '<strong>₱0.00</strong>';
        if (taxAmountEl) taxAmountEl.textContent = '₱0.00';
        orderTotalEl.innerHTML = '<strong>₱0.00</strong>';
        return;
    }

    const perPersonSubtotal = currentPersonCount * pricePerHead;
    const total = perPersonSubtotal;

    // Update all elements - ensure we're setting the text content correctly
    if (personCountDisplay) personCountDisplay.textContent = String(currentPersonCount);
    if (perPersonSubtotalEl) perPersonSubtotalEl.textContent = `₱${perPersonSubtotal.toFixed(2)}`;
    if (subtotalAmountEl) subtotalAmountEl.innerHTML = `<strong>₱${perPersonSubtotal.toFixed(2)}</strong>`;
    if (taxAmountEl) taxAmountEl.textContent = '₱0.00';
    if (orderTotalEl) {
        // Only update the amount, not the "TOTAL:" text
        orderTotalEl.innerHTML = `<strong>₱${total.toFixed(2)}</strong>`;
    }
}

// 🛒 Update cart display
function updateCartDisplay() {
    const summaryList = document.querySelector('.summary-list');
    const itemCount = document.querySelector('.item-count');

    summaryList.innerHTML = '';

    if (cart.length === 0) {
        summaryList.innerHTML = `
            <div class="empty-cart">
                <i class="bi bi-cart-x-fill" style="font-size: 3rem;"></i>
                <p>Your cart is empty<br>Add items from the menu to get started</p>
            </div>
        `;
        itemCount.textContent = '0 Items';
        return;
    }

    cart.forEach((item, index) => {
        const itemDiv = document.createElement('div');
        itemDiv.className = 'cart-item';
        const variantEditor = item.variants && item.variants.length > 0
            ? `<select class="cart-edit-field cart-variant" data-index="${index}">
                ${item.variants.map(v => `<option value="${escapeHtml(v.name)}" data-label="${escapeHtml(v.label)}" data-image="${escapeHtml(v.image || '')}" ${v.name === item.name ? 'selected' : ''}>${escapeHtml(v.label)}</option>`).join('')}
               </select>`
            : '';
        const servingEditor = item.servingSize
            ? `<select class="cart-edit-field cart-serving" data-index="${index}">
                <option value="Full cup" ${item.servingSize === 'Full cup' ? 'selected' : ''}>Full cup</option>
                <option value="Half cup" ${item.servingSize === 'Half cup' ? 'selected' : ''}>Half cup</option>
               </select>`
            : '';
        itemDiv.innerHTML = `
            <img src="${item.image}" alt="${item.name}"
                style="width:50px;height:50px;object-fit:cover;border-radius:8px;">
            <div style="flex:1;margin-left:10px;">
                <h5 style="margin:0;font-size:14px;">${item.name}</h5>
                ${variantEditor}
                ${servingEditor}
            </div>
            <div style="display:flex;align-items:center;gap:10px;">
                <button class="qty-btn minus" data-index="${index}">-</button>
                <span class="qty">${item.quantity}</span>
                <button class="qty-btn plus" data-index="${index}">+</button>
                <button class="remove-btn" data-index="${index}">
                    <i class="bi bi-trash"></i>
                </button>
            </div>
        `;
        summaryList.appendChild(itemDiv);
    });

    const totalItems = cart.reduce((sum, item) => sum + item.quantity, 0);
    itemCount.textContent = `${totalItems} Item${totalItems !== 1 ? 's' : ''}`;

    addCartEventListeners();
}

// 🔁 Add event listeners for cart quantity buttons
function addCartEventListeners() {
    // Use event delegation instead of adding multiple listeners
    const summaryList = document.querySelector('.summary-list');

    // Remove old listener if exists
    const oldList = summaryList.cloneNode(true);
    summaryList.parentNode.replaceChild(oldList, summaryList);

    // Add single delegated listener
    document.querySelector('.summary-list').addEventListener('click', function (e) {
        const target = e.target.closest('button');
        if (!target) return;

        const index = parseInt(target.getAttribute('data-index'));

        if (target.classList.contains('plus')) {
            const quantityLimit = getQuantityLimit();
            if (cart[index].isWingFlavor !== false && cart[index].quantity >= quantityLimit) {
                showNotification(`You can only order up to ${quantityLimit} pcs per flavor.`, 'error');
                return;
            }
            cart[index].quantity += 1;
            updateCartDisplay();
        }

        else if (target.classList.contains('minus')) {
            if (cart[index].quantity > 1) {
                cart[index].quantity -= 1;
            } else {
                cart.splice(index, 1);
            }
            updateCartDisplay();
        }

        else if (target.classList.contains('remove-btn') || target.closest('.remove-btn')) {
            cart.splice(index, 1);
            updateCartDisplay();
        }
    });

    document.querySelectorAll('.cart-variant').forEach(select => {
        select.addEventListener('change', function () {
            const index = parseInt(this.getAttribute('data-index'));
            const option = this.selectedOptions[0];
            cart[index].name = option.value;
            cart[index].variantLabel = option.getAttribute('data-label') || '';
            cart[index].image = option.getAttribute('data-image') || cart[index].image;
            updateCartDisplay();
        });
    });

    document.querySelectorAll('.cart-serving').forEach(select => {
        select.addEventListener('change', function () {
            const index = parseInt(this.getAttribute('data-index'));
            cart[index].servingSize = this.value;
            updateCartDisplay();
        });
    });
}

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// 🎯 Desktop drawer toggle on header click
document.addEventListener('DOMContentLoaded', () => {
    const summary = document.querySelector('.order-summary');
    const header = summary?.querySelector('.summary-header');
    
    if (header && summary) {
        header.addEventListener('click', function (e) {
            // Don't toggle if clicking on a button inside header
            if (e.target.tagName === 'BUTTON') return;
            
            // Toggle collapsed state only on desktop (max-width: 1200px)
            const isDesktop = window.matchMedia('(min-width: 1201px)').matches;
            if (isDesktop) {
                summary.classList.toggle('collapsed');
            }
        });
    }
});

