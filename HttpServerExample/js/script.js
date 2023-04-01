
window.addEventListener("load", function () {
    fetch("/app/ReverseText", {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify({ text: "abcdefg"}),
    })
        .then(response => response.text())
        .then(response => {
            var div = document.createElement("DIV");
            div.innerText = "Reverse text response: " + response;
            document.body.appendChild(div);
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
            var div = document.createElement("DIV");
            div.innerText = "Times Two response: " + response;
            document.body.appendChild(div);
        });
});