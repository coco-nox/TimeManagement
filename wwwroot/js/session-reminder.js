// The study-session reminder card (Pages/Shared/_SessionReminder.cshtml).
// Checks once a minute for a session starting soon and shows a card with
// three answers - OK, continue (opens the Tutor), I need more time (push
// back or move to a free slot), Cancel session (with a reason, logged as
// missed on Analytics). Every answer is saved through the Reminders page's
// handlers; the rules live server-side in SessionReminderService.
(function () {
    var root = document.getElementById('sessionReminder');
    var tokenInput = document.querySelector('#sessionReminderAntiForgery input[name="__RequestVerificationToken"]');
    if (!root || !tokenInput) {
        return;
    }

    var POLL_MS = 60 * 1000;
    var session = null;      // last session the server returned
    var view = 'default';    // default | more | cancel | continue | moved | cancelled
    var choice = null;       // selected move option's start
    var reason = null;       // selected cancel reason
    var lastMove = null;     // { previousStart, whenLabel } for Undo
    var busy = false;

    // ---- small DOM helpers ------------------------------------------------

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
        b.disabled = busy;
        b.addEventListener('click', onClick);
        return b;
    }

    function pill(text, selected, onClick, extraClass) {
        var b = button(text, 'session-reminder-pill' + (extraClass ? ' ' + extraClass : '') + (selected ? ' selected' : ''), onClick);
        b.setAttribute('aria-pressed', selected ? 'true' : 'false');
        return b;
    }

    function post(handler, fields) {
        var body = new FormData();
        Object.keys(fields).forEach(function (key) {
            body.append(key, fields[key] == null ? '' : fields[key]);
        });
        body.append('__RequestVerificationToken', tokenInput.value);
        return fetch(root.dataset.postUrl + '?handler=' + handler, { method: 'POST', body: body })
            .then(function (response) {
                return response.json().catch(function () { return { ok: false }; });
            });
    }

    function startsInText(minutes) {
        if (minutes > 1) {
            return 'Starting in ' + minutes + ' minutes';
        }
        if (minutes === 1) {
            return 'Starting in 1 minute';
        }
        if (minutes === 0) {
            return 'Starting now';
        }
        return 'Started ' + (-minutes) + ' minute' + (minutes === -1 ? '' : 's') + ' ago';
    }

    // ---- rendering ----------------------------------------------------------

    function render() {
        root.innerHTML = '';
        if (!session) {
            root.hidden = true;
            return;
        }
        root.hidden = false;

        var card = el('div', 'session-reminder-card');

        var header = el('div', 'session-reminder-header');
        var icon = el('span', 'session-reminder-icon');
        icon.innerHTML = '<svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="8"></circle><polyline points="12 9 12 13 14.5 15"></polyline><line x1="9" y1="2.5" x2="15" y2="2.5"></line></svg>';
        header.appendChild(icon);
        header.appendChild(el('div', 'session-reminder-eyebrow', startsInText(session.minutesUntil)));
        var close = button('', 'session-reminder-close', function () {
            post('Dismiss', { taskId: session.id });
            hide();
        });
        close.setAttribute('aria-label', 'Dismiss reminder');
        close.innerHTML = '<svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><line x1="6" y1="6" x2="18" y2="18"></line><line x1="18" y1="6" x2="6" y2="18"></line></svg>';
        header.appendChild(close);
        card.appendChild(header);

        var details = el('div', 'session-reminder-details');
        if (session.courseTitle) {
            var course = el('div', 'session-reminder-course');
            var dot = el('span', 'session-reminder-dot');
            dot.style.backgroundColor = session.colour;
            course.appendChild(dot);
            course.appendChild(document.createTextNode(session.courseTitle));
            details.appendChild(course);
        }
        details.appendChild(el('div', 'session-reminder-title', session.title));
        details.appendChild(el('div', 'session-reminder-meta', session.timeLabel + (session.forLabel ? ' · ' + session.forLabel : '')));
        card.appendChild(details);

        var body = el('div', 'session-reminder-body');
        ({
            'default': renderDefault,
            more: renderMore,
            cancel: renderCancel,
            'continue': renderContinue,
            moved: renderMoved,
            cancelled: renderCancelled
        }[view] || renderDefault)(body);
        card.appendChild(body);

        root.appendChild(card);
    }

    function renderDefault(body) {
        body.appendChild(el('div', 'session-reminder-question', 'Ready to start?'));
        body.appendChild(button('OK, continue — open Tutor', 'btn btn-primary session-reminder-wide', function () {
            busy = true;
            post('Continue', { taskId: session.id }).finally(function () {
                busy = false;
                setView('continue');
                // The server started this session's study timer - show its bar.
                window.dispatchEvent(new Event('flightpath:session-started'));
            });
        }));
        var row = el('div', 'session-reminder-row');
        row.appendChild(button('I need more time', 'btn btn-outline-secondary', function () {
            var first = (session.pushBack[0] || session.slots[0]);
            choice = first ? first.start : null;
            setView('more');
        }));
        row.appendChild(button('Cancel session', 'btn btn-outline-danger', function () {
            reason = null;
            setView('cancel');
        }));
        body.appendChild(row);
    }

    function allMoveOptions() {
        return session.pushBack.concat(session.slots);
    }

    function renderMore(body) {
        var options = allMoveOptions();
        if (options.length === 0) {
            body.appendChild(el('div', 'session-reminder-question', 'There\'s no free time to move this to'));
            body.appendChild(el('div', 'session-reminder-note', 'Pushing it back would run into another session, and there are no other available hours left this week. Paint more available time on the Calendar, or cancel the session instead.'));
            body.appendChild(button('Back', 'btn btn-outline-secondary align-self-start', function () { setView('default'); }));
            return;
        }

        if (session.pushBack.length > 0) {
            body.appendChild(el('div', 'session-reminder-question', 'How much more time do you need?'));
            var pills = el('div', 'session-reminder-pills');
            session.pushBack.forEach(function (option) {
                pills.appendChild(pill(option.label, choice === option.start, function () { choice = option.start; render(); }));
            });
            body.appendChild(pills);
        }

        if (session.slots.length > 0) {
            body.appendChild(el('div', 'session-reminder-label', session.pushBack.length > 0 ? 'Or move it to another free slot' : 'Move it to another free slot'));
            var slots = el('div', 'session-reminder-slots');
            session.slots.forEach(function (option) {
                var slot = pill('', choice === option.start, function () { choice = option.start; render(); }, 'session-reminder-slot');
                slot.appendChild(el('span', 'session-reminder-slot-label', option.label));
                slot.appendChild(el('span', 'session-reminder-slot-detail', option.detail));
                slots.appendChild(slot);
            });
            body.appendChild(slots);
            body.appendChild(el('div', 'session-reminder-note', 'Only times you\'ve painted as available on your Calendar are offered.'));
        }

        var selected = options.filter(function (o) { return o.start === choice; })[0] || options[0];
        var error = el('div', 'session-reminder-error');
        var row = el('div', 'session-reminder-row');
        row.appendChild(button(selected.moveLabel, 'btn btn-primary', function () {
            busy = true;
            render();
            post('Move', { taskId: session.id, start: selected.start }).then(function (result) {
                busy = false;
                if (result.ok) {
                    lastMove = { previousStart: result.previousStart, whenLabel: selected.whenLabel, leadMinutes: result.leadMinutes };
                    setView('moved');
                } else {
                    render();
                    showError(result.error || 'That didn\'t work - please try again.');
                }
            });
        }));
        row.appendChild(button('Back', 'btn btn-outline-secondary', function () { setView('default'); }));
        body.appendChild(row);
        body.appendChild(error);
    }

    function renderCancel(body) {
        var heading = el('div');
        heading.appendChild(el('div', 'session-reminder-question', 'Why are you cancelling?'));
        heading.appendChild(el('div', 'session-reminder-note', 'Your reason goes into your weekly review, so the plan can adjust.'));
        body.appendChild(heading);

        var pills = el('div', 'session-reminder-pills');
        session.reasons.forEach(function (name) {
            pills.appendChild(pill(name, reason === name, function () {
                reason = reason === name ? null : name;
                var note = document.getElementById('sessionReminderNote');
                var keep = note ? note.value : '';
                render();
                var newNote = document.getElementById('sessionReminderNote');
                if (newNote) {
                    newNote.value = keep;
                }
            }, 'danger'));
        });
        body.appendChild(pills);

        var label = el('label', 'session-reminder-label', 'Add a note (optional)');
        label.htmlFor = 'sessionReminderNote';
        body.appendChild(label);
        var note = el('textarea', 'form-control');
        note.id = 'sessionReminderNote';
        note.rows = 2;
        note.maxLength = 200;
        note.placeholder = 'e.g. lab ran late, will catch up on Saturday';
        body.appendChild(note);
        body.appendChild(el('div', 'session-reminder-note', 'It\'s logged as missed on Analytics, with your reason.'));

        var row = el('div', 'session-reminder-row');
        row.appendChild(button(reason ? 'Cancel session' : 'Cancel without a reason', 'btn btn-danger', function () {
            busy = true;
            render();
            post('Cancel', { taskId: session.id, reason: reason, note: note.value }).then(function () {
                busy = false;
                setView('cancelled');
            });
        }));
        row.appendChild(button('Keep session', 'btn btn-outline-secondary', function () { setView('default'); }));
        body.appendChild(row);
    }

    function renderContinue(body) {
        var done = el('div', 'session-reminder-done');
        done.innerHTML = '<svg aria-hidden="true" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><polyline points="5 12.5 10 17 19 7.5"></polyline></svg>';
        done.appendChild(document.createTextNode('Have a good session'));
        body.appendChild(done);
        body.appendChild(el('div', 'session-reminder-text', 'Your study timer has started - it\'s at the top of the page. The Tutor opens on ' + (session.courseTitle || 'your course') + ' so you can write, quiz yourself or ask questions while you work.'));
        var row = el('div', 'session-reminder-row');
        var open = el('a', 'btn btn-primary', 'Open Tutor');
        open.href = session.tutorUrl;
        row.appendChild(open);
        row.appendChild(button('Close', 'btn btn-outline-secondary', hide));
        body.appendChild(row);
    }

    function renderMoved(body) {
        body.appendChild(el('div', 'session-reminder-question', 'Moved to ' + lastMove.whenLabel));
        body.appendChild(el('div', 'session-reminder-text muted', 'You\'ll get another reminder ' + lastMove.leadMinutes + ' minutes before it starts. It\'s still the same session on your Calendar and Analytics.'));
        var row = el('div', 'session-reminder-row');
        row.appendChild(button('Undo', 'btn btn-outline-secondary', function () {
            post('Move', { taskId: session.id, start: lastMove.previousStart }).then(function (result) {
                if (result.ok) {
                    refresh(true);
                } else {
                    showError(result.error || 'That couldn\'t be undone.');
                }
            });
        }));
        row.appendChild(button('Close', 'btn btn-outline-secondary', hide));
        body.appendChild(row);
        body.appendChild(el('div', 'session-reminder-error'));
    }

    function renderCancelled(body) {
        body.appendChild(el('div', 'session-reminder-question danger', 'Session cancelled'));
        body.appendChild(el('div', 'session-reminder-text muted', 'Logged as missed on Analytics' + (reason ? '. Reason: ' + reason.toLowerCase() + '.' : '.')));
        var row = el('div', 'session-reminder-row');
        row.appendChild(button('Undo', 'btn btn-outline-secondary', function () {
            post('Uncancel', { taskId: session.id }).then(function () { refresh(true); });
        }));
        var analytics = el('a', 'btn btn-outline-secondary', 'Open Analytics');
        analytics.href = root.dataset.analyticsUrl;
        row.appendChild(analytics);
        body.appendChild(row);
    }

    function showError(text) {
        var error = root.querySelector('.session-reminder-error');
        if (error) {
            error.textContent = text;
        }
    }

    function setView(next) {
        view = next;
        render();
    }

    function hide() {
        session = null;
        view = 'default';
        render();
    }

    // ---- polling ------------------------------------------------------------

    // Only replaces what's shown while the card is idle (or empty), so a
    // student halfway through picking a reason isn't interrupted.
    function refresh(force) {
        if (!force && session && view !== 'default') {
            return;
        }

        fetch(root.dataset.upcomingUrl, { headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : { session: null }; })
            .then(function (data) {
                if (!force && session && view !== 'default') {
                    return;
                }
                var next = data.session;
                if (!next || !session || next.id !== session.id) {
                    view = 'default';
                }
                session = next;
                render();
            })
            .catch(function () { /* offline or signed out - try again next minute */ });
    }

    refresh(true);
    setInterval(function () { refresh(false); }, POLL_MS);
})();
