// The study-session timer (Pages/Shared/_StudySession.cshtml). While a
// session runs, a bar sits at the top of every page and this script sends a
// heartbeat every 30 seconds saying whether the student typed, scrolled,
// clicked or moved the mouse in FlightPath since the last one (and the tab
// is in view). The server turns those into active / offline / inactive time
// (StudySessionService) and says when to ask "Still studying?". Only
// activity inside FlightPath can be seen - which is why it asks rather
// than assumes.
(function () {
    var root = document.getElementById('studySession');
    var tokenInput = document.querySelector('#studySessionAntiForgery input[name="__RequestVerificationToken"]');
    if (!root || !tokenInput) {
        return;
    }

    var baseUrl = root.dataset.url;
    var HEARTBEAT_MS = 30 * 1000;
    var IDLE_POLL_MS = 60 * 1000;

    var session = null;          // last state from the server
    var stateAt = 0;             // Date.now() when it arrived (for the live clock)
    var lastActivity = Date.now();
    var heartbeatTimer = null;
    var pollTimer = null;
    var summary = null;          // end-of-session summary being shown
    var dialog = null;           // the open "Still studying?" / summary overlay

    // ---- helpers ----------------------------------------------------------

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text != null) {
            node.textContent = text;
        }
        return node;
    }

    function button(text, className, onClick) {
        var b = el('button', className, text);
        b.type = 'button';
        b.addEventListener('click', onClick);
        return b;
    }

    function post(handler, fields) {
        var body = new FormData();
        Object.keys(fields || {}).forEach(function (key) { body.append(key, fields[key]); });
        body.append('__RequestVerificationToken', tokenInput.value);
        return fetch(baseUrl + '?handler=' + handler, { method: 'POST', body: body })
            .then(function (response) { return response.json(); });
    }

    function clock(totalSeconds) {
        totalSeconds = Math.max(0, Math.floor(totalSeconds));
        var h = Math.floor(totalSeconds / 3600);
        var m = Math.floor((totalSeconds % 3600) / 60);
        var s = totalSeconds % 60;
        return h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    }

    function minutes(seconds) {
        return Math.round(seconds / 60);
    }

    // ---- activity -----------------------------------------------------------

    var lastMove = 0;
    function noteActivity(event) {
        // Mouse moves are throttled - they fire constantly.
        if (event && event.type === 'mousemove') {
            var now = Date.now();
            if (now - lastMove < 2000) {
                return;
            }
            lastMove = now;
        }
        lastActivity = Date.now();
    }
    ['keydown', 'pointerdown', 'mousemove', 'wheel', 'scroll', 'input', 'touchstart'].forEach(function (type) {
        window.addEventListener(type, noteActivity, { passive: true, capture: true });
    });

    function wasActive() {
        return document.visibilityState === 'visible' && Date.now() - lastActivity < HEARTBEAT_MS + 2000;
    }

    // ---- bar -----------------------------------------------------------------

    function renderBar() {
        root.innerHTML = '';
        if (!session) {
            root.hidden = true;
            return;
        }
        root.hidden = false;

        var bar = el('div', 'study-bar' + (session.isOnBreak ? ' on-break' : session.needsCheck ? ' idle' : ''));
        bar.setAttribute('role', 'status');
        bar.setAttribute('aria-label', 'Study session in progress');

        bar.appendChild(el('span', 'study-bar-dot'));

        var info = el('div', 'study-bar-info');
        info.appendChild(el('div', 'study-bar-state', session.isOnBreak ? 'On a break' : session.needsCheck ? 'No activity for ' + session.pendingMinutes + ' minutes' : 'Studying now'));
        info.appendChild(el('div', 'study-bar-title', session.title + (session.courseTitle && session.title.indexOf(session.courseTitle) === -1 ? ' · ' + session.courseTitle : '')));
        bar.appendChild(info);

        var times = el('div', 'study-bar-times');
        var big = el('div', 'study-bar-clock');
        big.id = 'studyClock';
        times.appendChild(big);
        times.appendChild(el('div', 'study-bar-split', 'Studied ' + minutes(session.studiedSeconds) + ' min · inactive ' + minutes(session.inactiveSeconds) + ' min'));
        bar.appendChild(times);

        var actions = el('div', 'study-bar-actions');
        if (session.isOnBreak) {
            actions.appendChild(button('I\'m back', 'btn btn-primary btn-sm', function () { act('Resume'); }));
        } else {
            actions.appendChild(button('Take a break', 'btn btn-outline-secondary btn-sm', function () { act('Break'); }));
        }
        actions.appendChild(button('End session', session.isOnBreak ? 'btn btn-outline-secondary btn-sm' : 'btn btn-primary btn-sm', endSession));
        bar.appendChild(actions);

        root.appendChild(bar);
        tick();
    }

    function tick() {
        var clockEl = document.getElementById('studyClock');
        if (!clockEl || !session) {
            return;
        }
        var elapsed = session.elapsedSeconds + (Date.now() - stateAt) / 1000;
        var text = clock(elapsed);
        if (session.plannedMinutes > 0) {
            text += ' of ' + clock(session.plannedMinutes * 60);
        }
        clockEl.textContent = text;
    }

    // ---- "Still studying?" -------------------------------------------------

    function closeDialog() {
        if (dialog) {
            dialog.remove();
            dialog = null;
        }
    }

    function openDialog(build, labelledBy) {
        closeDialog();
        dialog = el('div', 'study-overlay');
        var box = el('div', 'study-dialog');
        box.setAttribute('role', 'dialog');
        box.setAttribute('aria-modal', 'true');
        box.setAttribute('aria-labelledby', labelledBy);
        build(box);
        dialog.appendChild(box);
        document.body.appendChild(dialog);
        var first = box.querySelector('button, input');
        if (first) {
            first.focus();
        }
    }

    function showCheck() {
        if (dialog && dialog.dataset.kind === 'check') {
            return;
        }
        openDialog(function (box) {
            var title = el('h2', 'study-dialog-title', 'Still studying?');
            title.id = 'studyCheckTitle';
            box.appendChild(title);
            box.appendChild(el('p', 'study-dialog-text', 'There\'s been no typing, scrolling or clicking in FlightPath for ' + session.pendingMinutes + ' minutes during "' + session.title + '".'));

            var offline = button('', 'study-choice primary', function () { answer('offline'); });
            offline.appendChild(el('span', 'study-choice-main', 'Yes, I\'m working offline'));
            offline.appendChild(el('span', 'study-choice-sub', 'Reading, writing on paper - this time counts as studying'));
            box.appendChild(offline);

            var pause = button('', 'study-choice', function () { answer('break'); });
            pause.appendChild(el('span', 'study-choice-main', 'I took a break'));
            pause.appendChild(el('span', 'study-choice-sub', 'This time counts as inactive; the timer pauses until you\'re back'));
            box.appendChild(pause);

            box.appendChild(el('p', 'study-dialog-note', 'No answer in 2 minutes? It\'s counted as a break.'));
        }, 'studyCheckTitle');
        dialog.dataset.kind = 'check';
    }

    function answer(choice) {
        closeDialog();
        act('Answer', { choice: choice });
    }

    // ---- end of session -----------------------------------------------------

    function endSession() {
        if (!session || !window.confirm('End this study session?')) {
            return;
        }
        post('End', { sessionId: session.id }).then(function (result) {
            stopHeartbeat();
            session = null;
            renderBar();
            if (result.ok) {
                summary = result.summary;
                showSummary();
            }
            startPolling();
        });
    }

    function stat(value, label, kind) {
        var box = el('div', 'study-stat ' + (kind || ''));
        var number = el('div', 'study-stat-value', String(value));
        number.appendChild(el('span', 'study-stat-unit', ' min'));
        box.appendChild(number);
        box.appendChild(el('div', 'study-stat-label', label));
        return box;
    }

    function showSummary() {
        var s = summary;
        openDialog(function (box) {
            box.appendChild(el('div', 'study-dialog-eyebrow', 'Session complete'));
            var title = el('h2', 'study-dialog-title', s.title);
            title.id = 'studySummaryTitle';
            box.appendChild(title);
            if (s.plannedMinutes > 0 || s.courseTitle) {
                box.appendChild(el('p', 'study-dialog-text muted', [s.courseTitle, s.plannedMinutes > 0 ? s.plannedMinutes + ' min planned' : null].filter(Boolean).join(' · ')));
            }

            var stats = el('div', 'study-stats');
            stats.appendChild(stat(s.totalMinutes, 'Total time'));
            stats.appendChild(stat(s.activeMinutes + s.offlineMinutes, 'Active', 'good'));
            stats.appendChild(stat(s.inactiveMinutes, 'Inactive', 'warn'));
            box.appendChild(stats);

            var total = Math.max(1, s.activeMinutes + s.offlineMinutes + s.inactiveMinutes);
            var bar = el('div', 'study-split');
            bar.setAttribute('role', 'img');
            bar.setAttribute('aria-label', s.activeMinutes + ' minutes active in FlightPath, ' + s.offlineMinutes + ' offline, ' + s.inactiveMinutes + ' inactive');
            [['active', s.activeMinutes], ['offline', s.offlineMinutes], ['inactive', s.inactiveMinutes]].forEach(function (part) {
                if (part[1] > 0) {
                    var seg = el('div', 'study-split-' + part[0]);
                    seg.style.width = (part[1] * 100 / total) + '%';
                    bar.appendChild(seg);
                }
            });
            box.appendChild(bar);
            box.appendChild(el('div', 'study-split-legend', s.activeMinutes + ' min in FlightPath · ' + s.offlineMinutes + ' min offline (you said) · ' + s.inactiveMinutes + ' min inactive'));

            box.appendChild(el('div', 'study-dialog-eyebrow', 'What you got done'));
            var list = el('ul', 'study-done');
            [['Words written', s.wordsWritten],
             ['Checklist items ticked', s.checklistTicked],
             ['Quiz questions answered', s.quizAnswered ? s.quizAnswered + ' · ' + s.quizCorrect + ' correct' : 0],
             ['Questions asked to the Tutor', s.tutorQuestions]].forEach(function (row) {
                var item = el('li');
                item.appendChild(el('span', null, row[0]));
                item.appendChild(el('strong', null, String(row[1])));
                list.appendChild(item);
            });
            box.appendChild(list);

            // "Adjust times": count some inactive minutes as offline study.
            var adjust = el('div', 'study-adjust');
            adjust.hidden = true;
            if (s.inactiveMinutes > 0) {
                var label = el('label', null, 'Count ');
                var input = el('input', 'form-control form-control-sm');
                input.type = 'number';
                input.min = 0;
                input.max = s.inactiveMinutes;
                input.value = 0;
                input.id = 'studyAdjustMinutes';
                label.htmlFor = 'studyAdjustMinutes';
                adjust.appendChild(label);
                adjust.appendChild(input);
                adjust.appendChild(el('span', null, ' of the ' + s.inactiveMinutes + ' inactive minutes as studying offline'));
            }
            box.appendChild(adjust);

            var row = el('div', 'study-dialog-actions');
            var save = button('Save to Analytics', 'btn btn-primary', function () {
                var adjustInput = document.getElementById('studyAdjustMinutes');
                var offline = adjustInput && !adjust.hidden ? Math.max(0, Math.min(s.inactiveMinutes, parseInt(adjustInput.value, 10) || 0)) : 0;
                save.disabled = true;
                post('Save', { sessionId: s.id, offlineMinutes: offline }).then(function (result) {
                    closeDialog();
                    summary = null;
                    if (result.ok) {
                        toast('Saved to Analytics' + (s.hasCalendarSession ? ' - this session is logged as done.' : '.'), result.analyticsUrl);
                    }
                });
            });
            row.appendChild(save);
            if (s.inactiveMinutes > 0) {
                row.appendChild(button('Adjust times', 'btn btn-outline-secondary', function () { adjust.hidden = !adjust.hidden; }));
            }
            row.appendChild(button('Close', 'btn btn-link text-muted', function () { closeDialog(); summary = null; }));
            box.appendChild(row);
            box.appendChild(el('p', 'study-dialog-note', s.hasCalendarSession
                ? 'Saving logs the Calendar session as done, with the real times above.'
                : 'Saving adds this time to Analytics.'));
        }, 'studySummaryTitle');
        dialog.dataset.kind = 'summary';
    }

    function toast(text, linkUrl) {
        var note = el('div', 'study-toast');
        note.setAttribute('role', 'status');
        note.appendChild(document.createTextNode(text + ' '));
        if (linkUrl) {
            var link = el('a', null, 'Open Analytics');
            link.href = linkUrl;
            note.appendChild(link);
        }
        document.body.appendChild(note);
        setTimeout(function () { note.remove(); }, 8000);
    }

    // ---- talking to the server -----------------------------------------------

    function apply(state) {
        session = state;
        stateAt = Date.now();
        renderBar();
        if (session && session.needsCheck && !session.isOnBreak) {
            showCheck();
        } else if (dialog && dialog.dataset.kind === 'check') {
            closeDialog();
        }
        if (session) {
            stopPolling();
            startHeartbeat();
        } else {
            stopHeartbeat();
            startPolling();
        }
    }

    function act(handler, extra) {
        if (!session) {
            return;
        }
        var fields = extra || {};
        fields.sessionId = session.id;
        post(handler, fields).then(function (result) { apply(result.session); });
    }

    function heartbeat() {
        if (!session) {
            return;
        }
        post('Heartbeat', { sessionId: session.id, active: wasActive() })
            .then(function (result) { apply(result.session); })
            .catch(function () { /* offline - next beat will catch up */ });
    }

    function startHeartbeat() {
        if (!heartbeatTimer) {
            heartbeatTimer = setInterval(heartbeat, HEARTBEAT_MS);
        }
    }

    function stopHeartbeat() {
        clearInterval(heartbeatTimer);
        heartbeatTimer = null;
    }

    function refresh() {
        fetch(baseUrl + '?handler=Current', { headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : { session: null }; })
            .then(function (data) {
                if (data.session && !session) {
                    // Just opened, or started from another page/tab: this
                    // page load counts as activity.
                    noteActivity();
                }
                apply(data.session);
            })
            .catch(function () { });
    }

    // Without a session, check now and then for one started elsewhere
    // (another tab, a reminder's "OK, continue", the Dashboard).
    function startPolling() {
        if (!pollTimer) {
            pollTimer = setInterval(refresh, IDLE_POLL_MS);
        }
    }

    function stopPolling() {
        clearInterval(pollTimer);
        pollTimer = null;
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            noteActivity();
            if (session) {
                heartbeat();
            } else {
                refresh();
            }
        }
    });
    window.addEventListener('flightpath:session-started', refresh);

    setInterval(tick, 1000);
    refresh();
})();
