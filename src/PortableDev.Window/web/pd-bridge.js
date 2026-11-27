// window.pd, injected by the window shell into every page before the page scripts run.
(function () {
  if (!window.chrome || !window.chrome.webview) {
    return;
  }

  var nextId = 1;
  var callbacks = new Map();
  var handlers = new Set();

  function call(method, params) {
    return new Promise(function (resolve, reject) {
      var id = "c" + (nextId++);
      callbacks.set(id, { resolve: resolve, reject: reject });
      window.chrome.webview.postMessage({ id: id, method: method, params: params || {} });
    });
  }

  function dispatch(event) {
    handlers.forEach(function (entry) {
      try {
        if (entry.name === "evt") {
          entry.fn(event);
        } else if (entry.name === "evt:" + event.id) {
          entry.fn(event.event);
        }
      } catch (error) {
        console.error(error);
      }
    });
  }

  window.chrome.webview.addEventListener("message", function (messageEvent) {
    var message = messageEvent.data;
    if (!message || typeof message !== "object") {
      return;
    }
    if (message.type === "reply") {
      var pending = callbacks.get(message.id);
      if (!pending) {
        return;
      }
      callbacks.delete(message.id);
      if (message.ok) {
        pending.resolve(message.result);
      } else {
        pending.reject(message.error || { code: "ERR_IO", message: "bridge call failed" });
      }
      return;
    }
    if (message.type === "event") {
      dispatch({ id: message.taskId, type: "evt", event: message.event });
    }
  });

  window.pd = {
    version: "2.0.0",
    extension: (location.pathname.split("/").filter(Boolean)[0] || ""),
    theme: "__PD_THEME__",
    locale: "__PD_LOCALE__",

    exec: function (argv, opts) {
      return call("exec", { argv: argv, opts: opts || {} }).then(function (result) { return result.taskId; });
    },

    run: function (argv, opts) {
      return call("run", { argv: argv, opts: opts || {} }).then(function (result) {
        return { done: result.done || {}, events: result.events || [] };
      });
    },

    on: function (name, fn) {
      var entry = { name: name, fn: fn };
      handlers.add(entry);
      return function () { handlers.delete(entry); };
    },

    off: function (name, fn) {
      handlers.forEach(function (entry) {
        if (entry.name === name && entry.fn === fn) {
          handlers.delete(entry);
        }
      });
    },

    cancel: function (taskId) { return call("cancel", { taskId: taskId }); },
    reply: function (promptId, value) { return call("reply", { promptId: promptId, value: value }); },
    env: function () { return call("env", {}); },
    navigate: function (pageId) { return call("navigate", { page: pageId }); },
    setTheme: function (theme) { window.pd.theme = theme; return call("theme", { theme: theme }); },
  };
})();
