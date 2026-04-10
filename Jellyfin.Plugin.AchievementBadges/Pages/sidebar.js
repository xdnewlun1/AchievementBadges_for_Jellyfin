(function () {
    var ENTRY_ID = "achievementsSidebarEntry";
    var TARGET_URL = "/web/index.html#!/configurationpage?name=achievementbadges";

    function createNavEntry() {
        if (document.getElementById(ENTRY_ID)) return;
        if (document.getElementById("achievement-badges-nav-entry")) return;

        var sidebar =
            document.querySelector(".mainDrawer-scrollContainer .navMenuOptions") ||
            document.querySelector(".mainDrawer-scrollContainer .itemsContainer") ||
            document.querySelector(".mainDrawer-scrollContainer");

        if (!sidebar) return;

        var item = document.createElement("a");
        item.id = ENTRY_ID;
        item.href = TARGET_URL;
        item.className = "navMenuOption";
        item.style.display = "flex";
        item.style.alignItems = "center";
        item.style.gap = "1em";
        item.style.padding = "0.85em 1em";
        item.style.color = "inherit";
        item.style.textDecoration = "none";

        item.innerHTML =
            '<span class="material-icons navMenuOptionIcon" style="font-size:1.2em;">emoji_events</span>' +
            '<span class="navMenuOptionText">Achievements</span>';

        sidebar.appendChild(item);
    }

    function start() {
        createNavEntry();
        var observer = new MutationObserver(function () { createNavEntry(); });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start);
    } else {
        start();
    }
})();
