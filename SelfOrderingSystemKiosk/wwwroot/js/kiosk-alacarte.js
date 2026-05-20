// Cart array to store items
    let cart = [];
    const wingFlavorOptions = window.kioskAlaCarteConfig?.wingFlavorOptions ?? [];

    function escapeHtml(value) {
        return String(value ?? '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#039;');
    }

    function selectedOptions(select) {
        return select ? Array.from(select.selectedOptions).map(option => option.value) : [];
    }

    function selectedFlavorChips(container) {
        if (!container) return [];
        return Array.from(container.querySelectorAll('.flavor-chip.selected'))
            .map(button => button.getAttribute('data-flavor'))
            .filter(value => value);
    }

    function updateFlavorPickerState(container) {
        if (!container) return;
        const limit = parseInt(container.getAttribute('data-flavor-limit') || '0');
        const selected = selectedFlavorChips(container);
        const atLimit = limit > 0 && selected.length >= limit;
        container.querySelectorAll('.flavor-chip').forEach(button => {
            button.disabled = atLimit && !button.classList.contains('selected');
        });

        const count = container.querySelector('.flavor-count');
        if (count) {
            const emptyText = count.getAttribute('data-empty-text') || `Choose up to ${limit} flavors`;
            count.textContent = selected.length === 0
                ? emptyText
                : `${selected.length}/${limit} selected`;
        }

        const row = container.closest('.menu-row');
        const toggleText = row?.querySelector('.flavor-toggle span');
        if (toggleText) {
            toggleText.textContent = selected.length === 0
                ? 'Choose flavors'
                : `${selected.length}/${limit} flavors selected`;
        }
    }

    function flavorSlotGridHtml(item, index) {
        const current = item.flavors || [];
        const chips = wingFlavorOptions.map(flavor => `
            <button type="button" class="flavor-chip cart-flavor-chip ${current.includes(flavor) ? 'selected' : ''}" data-index="${index}" data-flavor="${escapeHtml(flavor)}">
                ${escapeHtml(flavor)}
            </button>
        `).join('');

        return `<button type="button" class="cart-flavor-toggle" data-index="${index}" aria-expanded="false">
                    <i class="bi bi-chevron-down"></i>
                    <span>Edit flavors</span>
                </button>
                <div class="cart-flavor-picker collapsed" data-index="${index}" data-flavor-limit="${item.flavorLimit}">
                    <div class="cart-flavor-grid">${chips}</div>
                    <small class="flavor-count" data-empty-text="Choose up to ${item.flavorLimit} flavor${item.flavorLimit === 1 ? '' : 's'}">Choose up to ${item.flavorLimit} flavor${item.flavorLimit === 1 ? '' : 's'}</small>
                </div>`;
    }

    function setPickerOpen(toggle, picker, isOpen) {
        if (!toggle || !picker) return;
        picker.classList.toggle('collapsed', !isOpen);
        toggle.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    }

    function cartKey(item) {
        return [
            item.name,
            item.variantLabel || '',
            (item.flavors || []).join('|')
        ].join('::');
    }

    function refreshVariantRow(row) {
        const select = row.querySelector('.variant-select');
        const button = row.querySelector('.add-to-cart');
        const priceEl = row.querySelector('.menu-price');
        const imageEl = row.querySelector('img');
        if (!select || !button) return;

        const option = select.selectedOptions[0];
        const price = parseFloat(option.getAttribute('data-price'));
        const image = option.getAttribute('data-image');
        button.setAttribute('data-name', option.value);
        button.setAttribute('data-price', String(price));
        if (image) {
            button.setAttribute('data-image', image);
            if (imageEl) imageEl.src = image;
        }
        if (priceEl) priceEl.textContent = `₱${price.toFixed(2)}`;
    }

    function readCardQuantity(row) {
        const control = row?.querySelector('.menu-quantity-control');
        const max = parseInt(control?.getAttribute('data-max-quantity') || '4', 10);
        const value = parseInt(control?.querySelector('.menu-qty-value')?.textContent || '4', 10);
        const limit = Number.isFinite(max) && max > 0 ? max : 4;
        return Number.isFinite(value) ? Math.min(Math.max(value, 1), limit) : limit;
    }

    function updateCardQuantity(control, quantity) {
        if (!control) return;

        const max = parseInt(control.getAttribute('data-max-quantity') || '4', 10);
        const limit = Number.isFinite(max) && max > 0 ? max : 4;
        const nextQuantity = Math.min(Math.max(quantity, 1), limit);
        const value = control.querySelector('.menu-qty-value');
        const minus = control.querySelector('.menu-qty-minus');
        const plus = control.querySelector('.menu-qty-plus');

        if (value) value.textContent = nextQuantity;
        if (minus) minus.disabled = nextQuantity <= 1;
        if (plus) plus.disabled = nextQuantity >= limit;
    }

    // Category filter functionality
    function applyMenuFilter(filter, activeButton) {
        document.querySelectorAll('.menu-tabs button').forEach(btn => btn.classList.remove('active'));
        if (activeButton) activeButton.classList.add('active');

        document.querySelectorAll('.menu-row').forEach(row => {
            row.style.display = (filter === 'all' || row.getAttribute('data-category') === filter) ? '' : 'none';
        });
    }

    document.querySelectorAll('.menu-tabs button').forEach(button => {
        button.addEventListener('click', function() {
            applyMenuFilter(this.getAttribute('data-filter'), this);
        });
    });

    const defaultAlaCarteFilter = Array.from(document.querySelectorAll('.menu-tabs button'))
        .find(button => button.getAttribute('data-filter') === 'Sulit Kap Meals');
    if (defaultAlaCarteFilter) {
        applyMenuFilter(defaultAlaCarteFilter.getAttribute('data-filter'), defaultAlaCarteFilter);
    }

    document.querySelectorAll('.variant-select').forEach(select => {
        select.addEventListener('change', function() {
            refreshVariantRow(this.closest('.menu-row'));
        });
    });

    document.querySelectorAll('.menu-quantity-control').forEach(control => {
        const initial = parseInt(control.querySelector('.menu-qty-value')?.textContent || '4', 10);
        updateCardQuantity(control, Number.isFinite(initial) ? initial : 4);
        control.addEventListener('click', function(e) {
            const button = e.target.closest('.menu-qty-btn');
            if (!button || button.disabled) return;

            const current = parseInt(this.querySelector('.menu-qty-value')?.textContent || '4', 10);
            const delta = button.classList.contains('menu-qty-plus') ? 1 : -1;
            updateCardQuantity(this, current + delta);
        });
    });

    document.querySelectorAll('.flavor-chip-picker').forEach(picker => {
        picker.addEventListener('click', function(e) {
            const chip = e.target.closest('.flavor-chip');
            if (!chip || chip.disabled) return;
            chip.classList.toggle('selected');
            updateFlavorPickerState(this);
        });
        updateFlavorPickerState(picker);
    });

    document.querySelectorAll('.flavor-toggle').forEach(toggle => {
        toggle.addEventListener('click', function() {
            const row = this.closest('.menu-row');
            const picker = row?.querySelector('.flavor-chip-picker');
            const isOpen = this.getAttribute('aria-expanded') !== 'true';
            setPickerOpen(this, picker, isOpen);
        });
    });

    let activeWingSet = null;
    let activeSulitMeal = null;

    function formatPeso(value) {
        return `₱${Number(value || 0).toFixed(2)}`;
    }

    function setSingleChoice(button, selector) {
        document.querySelectorAll(selector).forEach(choice => choice.classList.remove('selected'));
        button.classList.add('selected');
    }

    function selectedChoiceValue(selector, attrName) {
        return document.querySelector(`${selector}.selected`)?.getAttribute(attrName) || '';
    }

    function selectedWingSetOption() {
        const selected = document.querySelector('#wingPieceGrid .sulit-choice.selected');
        if (!selected || !activeWingSet) return null;
        return activeWingSet.options.find(option => option.name === selected.getAttribute('data-name')) || null;
    }

    function selectedWingSetFlavors() {
        return Array.from(document.querySelectorAll('#wingSetFlavorGrid .sulit-choice.selected'))
            .map(button => button.getAttribute('data-flavor'))
            .filter(Boolean);
    }

    function updateWingSetFlavorState() {
        const option = selectedWingSetOption();
        const limit = Number(option?.flavorLimit || 0);
        const selected = selectedWingSetFlavors();
        const atLimit = limit > 0 && selected.length >= limit;

        document.querySelectorAll('#wingSetFlavorGrid .sulit-choice').forEach(button => {
            button.disabled = atLimit && !button.classList.contains('selected');
        });

        const count = document.getElementById('wingSetFlavorCount');
        if (count) {
            count.textContent = selected.length === 0
                ? `Choose up to ${limit} flavor${limit === 1 ? '' : 's'}`
                : `${selected.length}/${limit} flavor${selected.length === 1 ? '' : 's'} selected`;
        }
    }

    function updateWingSetPreview() {
        const option = selectedWingSetOption();
        if (!option) return;

        const title = document.getElementById('wingSetTitle');
        const price = document.getElementById('wingSetPrice');
        const image = document.getElementById('wingSetImage');

        if (title) title.textContent = `${option.pieces} Piece Chicken Wings`;
        if (price) price.textContent = formatPeso(option.price);
        if (image && option.image) image.src = option.image;
        updateWingSetFlavorState();
    }

    function openWingSetScreen(button) {
        const options = JSON.parse(button.getAttribute('data-options') || '[]');
        if (options.length === 0) return;

        activeWingSet = {
            image: button.getAttribute('data-image') || '',
            options
        };

        const screen = document.getElementById('wingSetScreen');
        const image = document.getElementById('wingSetImage');
        const pieceGrid = document.getElementById('wingPieceGrid');

        if (image) image.src = activeWingSet.image;
        if (pieceGrid) {
            pieceGrid.innerHTML = options.map((option, index) => `
                <button type="button"
                        class="sulit-choice piece-choice ${index === 0 ? 'selected' : ''}"
                        data-name="${escapeHtml(option.name)}">
                    ${option.pieces} pcs - ${formatPeso(option.price)}
                </button>
            `).join('');
        }

        document.querySelectorAll('#wingSetFlavorGrid .sulit-choice').forEach(choice => {
            choice.classList.remove('selected');
            choice.disabled = false;
        });
        updateWingSetPreview();

        screen?.classList.add('active');
        screen?.setAttribute('aria-hidden', 'false');
    }

    function closeWingSetScreen() {
        const screen = document.getElementById('wingSetScreen');
        screen?.classList.remove('active');
        screen?.setAttribute('aria-hidden', 'true');
        activeWingSet = null;
    }

    function updateSulitMealPrice() {
        if (!activeSulitMeal) return;
        const selectedService = document.querySelector('#sulitServiceGrid .sulit-choice.selected');
        const price = Number(selectedService?.getAttribute('data-price') || '0');
        const priceEl = document.getElementById('sulitMealPrice');
        if (priceEl) priceEl.textContent = formatPeso(price);
    }

    function openSulitMealScreen(button) {
        const variants = JSON.parse(button.getAttribute('data-variants') || '[]');
        if (variants.length === 0) return;

        activeSulitMeal = {
            baseName: button.getAttribute('data-base-name') || 'Sulit Kap Meal',
            image: button.getAttribute('data-image') || '',
            variants
        };

        const screen = document.getElementById('sulitMealScreen');
        const image = document.getElementById('sulitMealImage');
        const title = document.getElementById('sulitMealTitle');
        const serviceGrid = document.getElementById('sulitServiceGrid');

        if (image) image.src = activeSulitMeal.image;
        if (title) title.textContent = activeSulitMeal.baseName;
        if (serviceGrid) {
            serviceGrid.innerHTML = variants.map((variant, index) => `
                <button type="button"
                        class="sulit-choice service-choice ${index === 0 ? 'selected' : ''}"
                        data-name="${escapeHtml(variant.name)}"
                        data-label="${escapeHtml(variant.label)}"
                        data-price="${variant.price}"
                        data-image="${escapeHtml(variant.image || activeSulitMeal.image)}">
                    ${escapeHtml(variant.label)} - ${formatPeso(variant.price)}
                </button>
            `).join('');
        }

        document.querySelectorAll('#sulitFlavorGrid .sulit-choice').forEach(choice => choice.classList.remove('selected'));
        document.querySelector('#sulitFlavorGrid .sulit-choice')?.classList.add('selected');
        document.querySelectorAll('.rice-choice').forEach(choice => choice.classList.remove('selected'));
        document.querySelector('.rice-choice')?.classList.add('selected');
        updateSulitMealPrice();

        screen?.classList.add('active');
        screen?.setAttribute('aria-hidden', 'false');
    }

    function closeSulitMealScreen() {
        const screen = document.getElementById('sulitMealScreen');
        screen?.classList.remove('active');
        screen?.setAttribute('aria-hidden', 'true');
        activeSulitMeal = null;
    }

    document.querySelectorAll('.customize-sulit-meal').forEach(button => {
        button.addEventListener('click', function() {
            openSulitMealScreen(this);
        });
    });

    document.querySelectorAll('.customize-wing-set').forEach(button => {
        button.addEventListener('click', function() {
            openWingSetScreen(this);
        });
    });

    document.querySelector('.wing-set-back-btn')?.addEventListener('click', closeWingSetScreen);

    document.getElementById('wingPieceGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.piece-choice');
        if (!button) return;
        setSingleChoice(button, '#wingPieceGrid .piece-choice');
        document.querySelectorAll('#wingSetFlavorGrid .sulit-choice').forEach(choice => {
            choice.classList.remove('selected');
            choice.disabled = false;
        });
        updateWingSetPreview();
    });

    document.getElementById('wingSetFlavorGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.sulit-choice');
        if (!button || button.disabled) return;

        button.classList.toggle('selected');
        updateWingSetFlavorState();
    });

    document.getElementById('addWingSetToCart')?.addEventListener('click', function() {
        const option = selectedWingSetOption();
        const flavors = selectedWingSetFlavors();
        if (!option) return;

        if (flavors.length === 0) {
            alert('Please choose at least 1 wing flavor.');
            return;
        }

        const newItem = {
            name: option.name,
            price: Number(option.price || 0),
            image: option.image || activeWingSet?.image || '',
            quantity: 1,
            flavorLimit: Number(option.flavorLimit || 0),
            flavors: flavors,
            variants: [],
            variantLabel: `${option.pieces} pcs`
        };

        const newKey = cartKey(newItem);
        const existingItem = cart.find(item => cartKey(item) === newKey);
        if (existingItem) {
            if (existingItem.quantity >= 4) {
                alert(`Maximum quantity of 4 per item allowed. You already have ${existingItem.quantity} of ${newItem.name} in your cart.`);
                return;
            }
            existingItem.quantity += 1;
        } else {
            cart.push(newItem);
        }

        updateCartDisplay();
        closeWingSetScreen();
    });

    document.querySelector('.sulit-meal-back-btn')?.addEventListener('click', closeSulitMealScreen);

    document.getElementById('sulitFlavorGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.sulit-choice');
        if (!button) return;
        setSingleChoice(button, '#sulitFlavorGrid .sulit-choice');
    });

    document.querySelectorAll('.rice-choice').forEach(button => {
        button.addEventListener('click', function() {
            setSingleChoice(this, '.rice-choice');
        });
    });

    document.getElementById('sulitServiceGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.service-choice');
        if (!button) return;
        setSingleChoice(button, '#sulitServiceGrid .service-choice');
        updateSulitMealPrice();
    });

    document.getElementById('addSulitMealToCart')?.addEventListener('click', function() {
        if (!activeSulitMeal) return;

        const service = document.querySelector('#sulitServiceGrid .service-choice.selected');
        const flavor = selectedChoiceValue('#sulitFlavorGrid .sulit-choice', 'data-flavor');
        const rice = selectedChoiceValue('.rice-choice', 'data-rice');

        if (!service || !flavor || !rice) {
            alert('Please complete the meal selections.');
            return;
        }

        const newItem = {
            name: service.getAttribute('data-name'),
            price: Number(service.getAttribute('data-price') || '0'),
            image: service.getAttribute('data-image') || activeSulitMeal.image,
            quantity: 1,
            flavorLimit: 0,
            flavors: [flavor, rice],
            variants: [],
            variantLabel: service.getAttribute('data-label') || ''
        };

        const newKey = cartKey(newItem);
        const existingItem = cart.find(item => cartKey(item) === newKey);
        if (existingItem) {
            if (existingItem.quantity >= 4) {
                alert(`Maximum quantity of 4 per item allowed. You already have ${existingItem.quantity} of ${newItem.name} in your cart.`);
                return;
            }
            existingItem.quantity += 1;
        } else {
            cart.push(newItem);
        }

        updateCartDisplay();
        closeSulitMealScreen();
    });

    // Add to cart functionality
    document.querySelectorAll('.add-to-cart').forEach(button => {
        button.addEventListener('click', function (e) {
            e.preventDefault();

            const name = this.getAttribute('data-name');
            const price = parseFloat(this.getAttribute('data-price'));
            const image = this.getAttribute('data-image');
            const row = this.closest('.menu-row');
            const flavorLimit = parseInt(this.getAttribute('data-flavor-limit') || '0');
            const variants = JSON.parse(this.getAttribute('data-variants') || '[]');
            const selectedVariant = variants.find(v => v.name === name);
            const flavors = selectedFlavorChips(row?.querySelector('.flavor-chip-picker'));
            const isWingFlavor = this.getAttribute('data-is-wing-flavor') === 'true';
            const requestedQuantity = isWingFlavor ? readCardQuantity(row) : 1;

            if (flavorLimit > 0) {
                if (flavors.length === 0) {
                    const picker = row?.querySelector('.flavor-chip-picker');
                    const toggle = row?.querySelector('.flavor-toggle');
                    setPickerOpen(toggle, picker, true);
                    alert(`Please choose at least 1 wing flavor before adding this set.`);
                    return;
                }
            }

            const newItem = {
                name: name,
                price: price,
                image: image,
                quantity: requestedQuantity,
                flavorLimit: flavorLimit,
                flavors: flavors,
                variants: variants,
                variantLabel: selectedVariant?.label || ''
            };

            // Check if item already exists in cart
            const newKey = cartKey(newItem);
            const existingItem = cart.find(item => cartKey(item) === newKey);

            if (existingItem) {
                // Limit quantity to 4 per item for ala carte
                if (existingItem.quantity + requestedQuantity > 4) {
                    alert(`Maximum quantity of 4 per item allowed. You already have ${existingItem.quantity} of ${name} in your cart.`);
                    return;
                }
                existingItem.quantity += requestedQuantity;
            } else {
                cart.push(newItem);
            }

            updateCartDisplay();

            // Visual feedback
            this.textContent = 'Added!';
            this.style.backgroundColor = '#28a745';
            setTimeout(() => {
                this.textContent = 'Add to cart';
                this.style.backgroundColor = '';
            }, 500);
        });
    });

    // Update cart display
    function updateCartDisplay() {
        const summaryList = document.querySelector('.summary-list');
        const itemCount = document.querySelector('.item-count');
        const orderTotal = document.querySelector('.order-total');

        summaryList.innerHTML = '';

        if (cart.length === 0) {
            summaryList.innerHTML = `
                <div class="empty-cart">
                    <i class="bi bi-cart-x-fill" style="font-size: 3rem;"></i>
                    <p>Your cart is empty<br>Add items from the menu to get started</p>
                </div>
            `;
            itemCount.textContent = '0 Items';
            orderTotal.textContent = 'TOTAL: ₱0.00';
            return;
        }

        cart.forEach((item, index) => {
            const itemDiv = document.createElement('div');
            itemDiv.className = 'cart-item';
            const isMaxQuantity = item.quantity >= 4;
            const plusButtonDisabled = isMaxQuantity ? 'disabled style="opacity:0.5;cursor:not-allowed;"' : '';
            const maxQuantityIndicator = isMaxQuantity ? '<small style="color:#e74c3c;font-size:0.75em;display:block;">Max: 4</small>' : '';
            const variantEditor = item.variants && item.variants.length > 0
                ? `<select class="cart-edit-field cart-variant" data-index="${index}">
                    ${item.variants.map(v => `<option value="${escapeHtml(v.name)}" data-price="${v.price}" data-label="${escapeHtml(v.label)}" data-image="${escapeHtml(v.image || '')}" ${v.name === item.name ? 'selected' : ''}>${escapeHtml(v.label)} - ₱${Number(v.price).toFixed(2)}</option>`).join('')}
                   </select>`
                : '';
            const flavorEditor = item.flavorLimit > 0
                ? flavorSlotGridHtml(item, index)
                : '';
            const flavorSummary = item.flavors && item.flavors.length > 0
                ? `<p style="margin:2px 0;color:#666;font-size:0.82rem;">Flavors: ${escapeHtml(item.flavors.join(', '))}</p>`
                : '';
            itemDiv.innerHTML = `
                <img src="${item.image}" alt="${item.name}" style="width:50px;height:50px;object-fit:cover;border-radius:8px;">
                <div style="flex:1;margin-left:10px;">
                    <h5 style="margin:0;font-size:14px;">${escapeHtml(item.name)}</h5>
                    ${variantEditor}
                    ${flavorEditor}
                    ${flavorSummary}
                    <p style="margin:0;color:#666;">₱${item.price.toFixed(2)}</p>
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
        const totalPrice = cart.reduce((sum, item) => sum + (item.price * item.quantity), 0);

        itemCount.textContent = `${totalItems} Item${totalItems !== 1 ? 's' : ''}`;
        orderTotal.textContent = `TOTAL: ₱${totalPrice.toFixed(2)}`;

        addCartEventListeners();
    }

    // Add event listeners for cart buttons
    function addCartEventListeners() {
        document.querySelectorAll('.qty-btn.plus').forEach(btn => {
            btn.addEventListener('click', function() {
                // Don't process if button is disabled
                if (this.disabled) {
                    return;
                }
                const index = parseInt(this.getAttribute('data-index'));
                // Limit quantity to 4 per item for ala carte
                if (cart[index].quantity >= 4) {
                    alert(`Maximum quantity of 4 per item allowed. You already have ${cart[index].quantity} of ${cart[index].name} in your cart.`);
                    return;
                }
                cart[index].quantity += 1;
                updateCartDisplay();
            });
        });

        document.querySelectorAll('.qty-btn.minus').forEach(btn => {
            btn.addEventListener('click', function() {
                const index = parseInt(this.getAttribute('data-index'));
                if (cart[index].quantity > 1) {
                    cart[index].quantity -= 1;
                } else {
                    cart.splice(index, 1);
                }
                updateCartDisplay();
            });
        });

        document.querySelectorAll('.remove-btn').forEach(btn => {
            btn.addEventListener('click', function() {
                const index = parseInt(this.getAttribute('data-index'));
                cart.splice(index, 1);
                updateCartDisplay();
            });
        });

        document.querySelectorAll('.cart-variant').forEach(select => {
            select.addEventListener('change', function() {
                const index = parseInt(this.getAttribute('data-index'));
                const option = this.selectedOptions[0];
                cart[index].name = option.value;
                cart[index].price = parseFloat(option.getAttribute('data-price'));
                cart[index].variantLabel = option.getAttribute('data-label') || '';
                cart[index].image = option.getAttribute('data-image') || cart[index].image;
                updateCartDisplay();
            });
        });

        document.querySelectorAll('.cart-flavor-picker').forEach(picker => {
            updateFlavorPickerState(picker);
            picker.addEventListener('click', function(e) {
                const chip = e.target.closest('.cart-flavor-chip');
                if (!chip || chip.disabled) return;

                const index = parseInt(chip.getAttribute('data-index'));
                chip.classList.toggle('selected');
                cart[index].flavors = selectedFlavorChips(this);
                updateCartDisplay();
            });
        });

        document.querySelectorAll('.cart-flavor-toggle').forEach(toggle => {
            toggle.addEventListener('click', function() {
                const index = this.getAttribute('data-index');
                const picker = document.querySelector(`.cart-flavor-picker[data-index="${index}"]`);
                const isOpen = this.getAttribute('aria-expanded') !== 'true';
                setPickerOpen(this, picker, isOpen);
            });
        });
    }

    // Confirm order button
           document.querySelector('.confirm-btn').addEventListener('click', function () {
        if (cart.length === 0) {
            alert('Please add items to your cart first!');
            return;
        }

        // Validate quantities before submitting (max 4 per item for ala carte)
        const invalidItems = cart.filter(item => item.quantity > 4);
        if (invalidItems.length > 0) {
            alert('Some items exceed the maximum quantity of 4 per item. Please adjust your order.');
            return;
        }

        const missingFlavorItems = cart.filter(item => item.flavorLimit > 0 && (!item.flavors || item.flavors.length === 0));
        if (missingFlavorItems.length > 0) {
            alert('Please choose wing flavors for every wing set in your cart.');
            return;
        }

        const orderItems = cart.map(item => ({
            itemName: item.flavors && item.flavors.length > 0
                ? `${item.name} (Flavors: ${item.flavors.join(', ')})`
                : item.name,
            price: item.price,
            quantity: item.quantity
        }));

        document.querySelector('.loader-overlay').style.display = 'flex';
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

        fetch('/Customer/Kiosk/ConfirmOrder?orderType=AlaCarte', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            body: JSON.stringify(orderItems)
        })
        .then(response => response.json())
        .then(data => {
            document.querySelector('.loader-overlay').style.display = 'none';

            if (data.success) {
                window.location.href = `/Customer/Kiosk/Confirmation?orderNumber=${encodeURIComponent(data.orderNumber)}&accessToken=${encodeURIComponent(data.accessToken || '')}`;
            } else {
                alert('Error: ' + data.message);
            }
        })
        .catch(error => {
            document.querySelector('.loader-overlay').style.display = 'none';
            alert('Error: ' + error);
        });

    });

