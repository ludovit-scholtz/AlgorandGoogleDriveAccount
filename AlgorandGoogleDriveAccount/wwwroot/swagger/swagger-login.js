// Injected into Swagger UI (see Program.cs -> app.UseSwaggerUI(...InjectJavascript...)).
// [Authorize] endpoints here use the same Google session cookie as the rest of the site, not a
// bearer token, so there is nothing for Swagger UI's built-in "Authorize" dialog to collect.
// Instead this adds a real "Login with Google" link that runs the normal OAuth challenge and
// returns to this page; once the session cookie is set, "Try it out" calls send it automatically
// because they are same-origin requests.
(function () {
    function addAuthLinks() {
        var topbar = document.querySelector('.topbar .wrapper') || document.querySelector('.topbar');
        if (!topbar || document.getElementById('biatec-auth-links')) {
            return false;
        }

        var redirectUri = encodeURIComponent(window.location.pathname + window.location.search);

        var container = document.createElement('div');
        container.id = 'biatec-auth-links';
        container.style.cssText = 'display:flex;align-items:center;gap:12px;margin-left:auto;padding-right:12px;';

        var login = document.createElement('a');
        login.href = '/api/drive/login?redirectUri=' + redirectUri;
        login.textContent = 'Login with Google';
        login.style.cssText = 'color:#fff;font-weight:600;text-decoration:none;';

        var logout = document.createElement('a');
        logout.href = '/api/drive/logout?redirectUri=' + redirectUri;
        logout.textContent = 'Logout';
        logout.style.cssText = 'color:#fff;font-weight:600;text-decoration:none;';

        container.appendChild(login);
        container.appendChild(logout);
        topbar.appendChild(container);
        return true;
    }

    if (!addAuthLinks()) {
        var observer = new MutationObserver(function () {
            if (addAuthLinks()) {
                observer.disconnect();
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }
})();
