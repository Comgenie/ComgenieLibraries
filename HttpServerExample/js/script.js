
var addMessage = function (message) {
    var div = document.createElement("DIV");
    div.innerText = message;
    document.body.appendChild(div);
};

window.addEventListener("load", function () {
    // Application route examples
    fetch("/app/ReverseText", {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify({ text: "abcdefg"}),
    })
        .then(response => response.text())
        .then(response => {
            addMessage("Reverse text response: " + response);
        });

    fetch("/app/TimesTwo", {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify({ dto: { Number: 21 } }),
    })
        .then(response => response.text())
        .then(response => {
            addMessage("Times Two response: " + response);
        });

    // Websocket route example
    var prefix = window.location.origin.replace(/^http/, 'ws');
    var ws = new WebSocket(prefix + "/websocket");
    ws.onopen = function (e) {
        addMessage("Websocket opened");
        ws.send("Test message send to websocket");
    };
    ws.onmessage = (e) => {
        addMessage("Websocket response: " + e.data);
    };
    ws.onerror = function (e) {
        addMessage("Websocket error: " + e.data);
    };
    ws.onclose = function (e) {
        addMessage("Websocket closed");
    };
});