document.addEventListener('DOMContentLoaded', function () {
    // ---- Mobile sidebar ----
    var sidebar = document.getElementById('sidebar');
    var backdrop = document.getElementById('sidebar-backdrop');
    var toggle = document.getElementById('sidebar-toggle');
    function closeSidebar() {
        if (!sidebar) return;
        sidebar.classList.add('-translate-x-full');
        backdrop && backdrop.classList.add('hidden');
    }
    if (toggle && sidebar) {
        toggle.addEventListener('click', function () {
            var open = !sidebar.classList.contains('-translate-x-full');
            if (open) { closeSidebar(); } else { sidebar.classList.remove('-translate-x-full'); backdrop && backdrop.classList.remove('hidden'); }
        });
        backdrop && backdrop.addEventListener('click', closeSidebar);
    }

    // ---- User menu ----
    var menuToggle = document.getElementById('user-menu-toggle');
    var menu = document.getElementById('user-menu');
    if (menuToggle && menu) {
        menuToggle.addEventListener('click', function (e) { e.stopPropagation(); menu.classList.toggle('hidden'); });
        document.addEventListener('click', function (e) { if (!menu.contains(e.target)) menu.classList.add('hidden'); });
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') menu.classList.add('hidden'); });
    }

    // ---- Dismissable alerts ----
    document.querySelectorAll('[data-alert-close]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var alert = btn.closest('[data-alert]');
            if (!alert) return;
            alert.classList.add('is-hiding');
            setTimeout(function () { alert.remove(); }, 160);
        });
    });

    // ---- Placeholder chips: insert {{Column}} at the cursor of the template editor ----
    var editor = document.getElementById('BodyHtml');
    if (editor) {
        document.querySelectorAll('.placeholder-chip').forEach(function (chip) {
            chip.addEventListener('click', function () {
                var token = '{{' + chip.getAttribute('data-placeholder') + '}}';
                var start = editor.selectionStart || 0;
                var end = editor.selectionEnd || 0;
                var value = editor.value;
                editor.value = value.substring(0, start) + token + value.substring(end);
                editor.focus();
                editor.selectionStart = editor.selectionEnd = start + token.length;
            });
        });
    }

    // ---- File inputs: show the chosen file name ----
    document.querySelectorAll('[data-file-input]').forEach(function (input) {
        var label = document.querySelector('[data-file-name="' + input.id + '"]');
        if (!label) return;
        input.addEventListener('change', function () {
            label.textContent = input.files && input.files.length ? input.files[0].name : 'No file selected';
        });
    });
});
