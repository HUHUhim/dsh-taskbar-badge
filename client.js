window.__ModuleLoader__.load({
  id: "dsh-taskbar-badge",
  factory: (require) => {
    var module = { exports: {} };
    var exports = module.exports;
    Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });

    var react = require("react");
    var PREFIX = /^\[\d+\]\s/;

    function UnreadTitleReporter(props) {
      var useSessionStatus = props.useSessionStatus;
      var useSessions = props.useSessions;

      var statuses = typeof useSessionStatus === "function"
        ? useSessionStatus(function (snapshot) { return snapshot; })
        : undefined;
      var sessions = typeof useSessions === "function"
        ? useSessions(function (snapshot) { return snapshot.byId; })
        : undefined;

      var count = react.useMemo(function () {
        if (!statuses || typeof statuses.forEach !== "function") return 0;
        var total = 0;
        statuses.forEach(function (status, id) {
          if (!status || status.completionUnread !== true) return;
          var row = sessions ? sessions[id] : undefined;
          if (!row || row.origin === "subagent" || row.archived === true) return;
          total += 1;
        });
        return total;
      }, [statuses, sessions]);

      react.useEffect(function () {
        var titleElement = document.querySelector("title");
        if (titleElement === null) return;

        var apply = function () {
          var base = document.title.replace(PREFIX, "");
          var next = count > 0 ? "[" + count + "] " + base : base;
          if (document.title !== next) document.title = next;
        };

        var observer = new MutationObserver(apply);
        observer.observe(titleElement, {
          childList: true,
          characterData: true,
          subtree: true
        });
        apply();

        return function () {
          observer.disconnect();
        };
      }, [count]);

      return null;
    }

    var inject = ["slots"];

    function apply(ctx) {
      ctx.slots.inject("shell.overlay", function () {
        return ctx.slots.register({
          name: "shell.overlay",
          id: "dsh-taskbar-badge",
          order: 950
        }, UnreadTitleReporter);
      });
    }

    exports.name = "dsh-taskbar-badge";
    exports.inject = inject;
    exports.apply = apply;
    return module.exports;
  }
});
