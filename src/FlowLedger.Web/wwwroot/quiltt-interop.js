// Opens the Quiltt Connector inside the app. The server mints a short-lived, profile-scoped session token;
// the Quiltt API key never reaches the browser. Resolves { status: 'success'|'cancelled'|'error', connectionId? }.
window.flowledgerQuiltt = {
    _loading: null,

    _load(scriptUrl) {
        if (window.Quiltt) return Promise.resolve();
        // Only ever load over https from the URL the server configured.
        if (!/^https:\/\//i.test(scriptUrl)) return Promise.reject(new Error('Quiltt script URL must be https.'));
        this._loading ??= new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = scriptUrl;
            s.async = true;
            s.onload = () => (window.Quiltt ? resolve() : reject(new Error('Quiltt connector did not initialise.')));
            s.onerror = () => { this._loading = null; reject(new Error('Could not load the Quiltt connector.')); };
            document.head.appendChild(s);
        });
        return this._loading;
    },

    // Repairs an existing connection in place (https://www.quiltt.dev/connector/reconnect).
    async reconnect(scriptUrl, connectorId, token, connectionId) {
        await this._load(scriptUrl);
        window.Quiltt.authenticate(token);
        return await new Promise((resolve) => {
            const connector = window.Quiltt.reconnect(connectorId, {
                connectionId,
                onExitSuccess: (metadata) => resolve({ status: 'success', connectionId: metadata?.connectionId ?? connectionId }),
                onExitAbort: () => resolve({ status: 'cancelled', connectionId: null }),
                onExitError: () => resolve({ status: 'error', connectionId: null }),
            });
            connector.open();
        });
    },

    async open(scriptUrl, connectorId, token) {
        await this._load(scriptUrl);
        window.Quiltt.authenticate(token);
        return await new Promise((resolve) => {
            const connector = window.Quiltt.connect(connectorId, {
                onExitSuccess: (metadata) => resolve({ status: 'success', connectionId: metadata?.connectionId ?? null }),
                onExitAbort: () => resolve({ status: 'cancelled', connectionId: null }),
                onExitError: () => resolve({ status: 'error', connectionId: null }),
            });
            connector.open();
        });
    },
};
