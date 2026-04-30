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
    return 4;
}

function getQuantityLimit() {
    return 4;
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

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function cartKey(item) {
    return [
        item.name
    ].join('::');
}

function refreshVariantRow(row) {
    const select = row?.querySelector('.variant-select');
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
}

function readCardQuantity(row) {
    const control = row?.querySelector('.menu-quantity-control');
    const max = parseInt(control?.getAttribute('data-max-quantity') || String(getQuantityLimit()), 10);
    const value = parseInt(control?.querySelector('.menu-qty-value')?.textContent || String(getQuantityLimit()), 10);
    const limit = Number.isFinite(max) && max > 0 ? max : getQuantityLimit();
    return Number.isFinite(value) ? Math.min(Math.max(value, 1), limit) : limit;
}

function updateCardQuantity(control, quantity) {
    if (!control) return;

    const max = parseInt(control.getAttribute('data-max-quantity') || String(getQuantityLimit()), 10);
    const limit = Number.isFinite(max) && max > 0 ? max : getQuantityLimit();
    const nextQuantity = Math.min(Math.max(quantity, 1), limit);
    const value = control.querySelector('.menu-qty-value');
    const minus = control.querySelector('.menu-qty-minus');
    const plus = control.querySelector('.menu-qty-plus');

    if (value) value.textContent = nextQuantity;
    if (minus) minus.disabled = nextQuantity <= 1;
    if (plus) plus.disabled = nextQuantity >= limit;
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

            fetch(`/Customer/Kiosk/SaveOrderingSession?personCount=${personCount}`, {
                method: 'POST'
            }).catch(err => console.error('Error saving ordering session:', err));
            
            // Update order summary with a small delay to ensure DOM is ready
            setTimeout(() => {
                updateOrderSummary();
            }, 100);

            closeModal("personModal");
            showRulesModal();
        });
    }

    document.querySelectorAll('.variant-select').forEach(select => {
        select.addEventListener('change', function () {
            refreshVariantRow(this.closest('.menu-row'));
        });
    });

    document.querySelectorAll('.menu-quantity-control').forEach(control => {
        const initial = parseInt(control.querySelector('.menu-qty-value')?.textContent || String(getQuantityLimit()), 10);
        updateCardQuantity(control, Number.isFinite(initial) ? initial : getQuantityLimit());
        control.addEventListener('click', function (e) {
            const button = e.target.closest('.menu-qty-btn');
            if (!button || button.disabled) return;

            const current = parseInt(this.querySelector('.menu-qty-value')?.textContent || String(getQuantityLimit()), 10);
            const delta = button.classList.contains('menu-qty-plus') ? 1 : -1;
            updateCardQuantity(this, current + delta);
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
            const requestedQuantity = isWingFlavor ? readCardQuantity(this.closest('.menu-row')) : 1;

            const newItem = {
                name,
                image,
                quantity: requestedQuantity,
                isWingFlavor
            };
            const existingItem = cart.find(item => cartKey(item) === cartKey(newItem));
            const flavorLimit = getFlavorLimit();
            const quantityLimit = getQuantityLimit();
            const selectedWingFlavorCount = cart.filter(item => item.isWingFlavor !== false).length;

            if (existingItem) {
                if (isWingFlavor && existingItem.quantity + requestedQuantity > quantityLimit) {
                    showNotification(`You can only add up to ${quantityLimit} pieces per wing flavor.`, 'error');
                    return;
                }
                existingItem.quantity += requestedQuantity;
            } else {
                if (isWingFlavor && selectedWingFlavorCount >= flavorLimit) {
                    showNotification(`You can only choose up to ${flavorLimit} wing flavors per order.`, 'error');
                    return;
                }
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
});

// 📋 Show the rules modal dynamically
function showRulesModal() {
    const rulesBody = document.getElementById("rulesBody");

    let message = "";
    if (personCount <= 2) {
        message = `
        <b>For 1–2 Customers:</b><br>
        • You can choose up to <b>4 wing flavors</b> per order.<br>
        • Maximum of <b>4 pieces</b> per wing flavor.<br>
        • Serving time is approximately <b>8 - 12 minutes</b>.<br>
        • Rice, red iced tea, gravy, nachos, potato thins, regular pasta, coffee, and tea are included.
        `;
    } else if (personCount <= 6) {
        message = `
        <b>For 3–6 Customers:</b><br>
        • You can choose up to <b>4 wing flavors</b> per order.<br>
        • Maximum of <b>4 pieces</b> per wing flavor.<br>
        • Serving time is approximately <b>8 - 12 minutes</b>.<br>
        • Rice, red iced tea, gravy, nachos, potato thins, regular pasta, coffee, and tea are included.
        `;
    } else {
        message = `
        <b>For 7+ Customers:</b><br>
        • You can choose up to <b>4 wing flavors</b> per order.<br>
        • Maximum of <b>4 pieces</b> per wing flavor.<br>
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
    const taxAmountEl = document.getElementById('tax-amount');
    const orderTotalEl = document.querySelector('.order-total');
    
    // Check if elements exist (for Unlimited menu only)
    if (!personCountDisplay || !perPersonSubtotalEl || !orderTotalEl) {
        return; // Not on Unlimited menu page
    }
    
    // Always use the current personCount value
    const currentPersonCount = personCount || 0;
    
    if (currentPersonCount === 0) {
        personCountDisplay.textContent = '0';
        perPersonSubtotalEl.textContent = '₱0.00';
        if (taxAmountEl) taxAmountEl.textContent = '₱0.00';
        orderTotalEl.innerHTML = '<strong>₱0.00</strong>';
        return;
    }

    const perPersonSubtotal = currentPersonCount * pricePerHead;
    const total = perPersonSubtotal;

    // Update all elements - ensure we're setting the text content correctly
    if (personCountDisplay) personCountDisplay.textContent = String(currentPersonCount);
    if (perPersonSubtotalEl) perPersonSubtotalEl.textContent = `₱${perPersonSubtotal.toFixed(2)}`;
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
        const quantityLimit = getQuantityLimit();
        const isMaxQuantity = item.isWingFlavor !== false && item.quantity >= quantityLimit;
        const plusButtonDisabled = isMaxQuantity ? 'disabled style="opacity:0.5;cursor:not-allowed;"' : '';
        const wingOrderNote = item.isWingFlavor !== false
            ? `<small style="color:#2e7d32;font-size:0.75em;display:block;">Max 4 pieces per flavor</small>`
            : '';
        const maxQuantityIndicator = isMaxQuantity
            ? `<small style="color:#e74c3c;font-size:0.75em;display:block;">Max: ${quantityLimit} pieces</small>`
            : '';
        itemDiv.innerHTML = `
            <img src="${item.image}" alt="${item.name}"
                style="width:50px;height:50px;object-fit:cover;border-radius:8px;">
            <div style="flex:1;margin-left:10px;">
                <h5 style="margin:0;font-size:14px;">${escapeHtml(item.name)}</h5>
                ${wingOrderNote}
                ${maxQuantityIndicator}
            </div>
            <div style="display:flex;align-items:center;gap:10px;">
                <button class="qty-btn minus" data-index="${index}">-</button>
                <span class="qty">${item.quantity}</span>
                <button class="qty-btn plus" data-index="${index}" ${plusButtonDisabled}>+</button>
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
                showNotification(`You can only add up to ${quantityLimit} pieces per wing flavor.`, 'error');
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
}

