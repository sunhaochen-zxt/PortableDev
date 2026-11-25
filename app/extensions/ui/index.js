// The UI extension provides pages only; it never runs business logic.
const { connect, ready } = require("./protocol");

connect({
  onMessage(message, send) {
    if (message.kind === "hello.ok") {
      ready(send, []);
    }
  },
});
