// Links out of the app. Routes to school open in a panel that slides over the 3D map, on the same page:
// the routes page (routes/) in a frame, since Google's routes may only be shown on a Google map.
mergeInto(LibraryManager.library, {
  OureaRoutesPanel: function (ptr) {
    var url = new URL(UTF8ToString(ptr), window.location.href).href;
    var p = document.getElementById("ourea-routes");
    if (!p) {
      var css = document.createElement("style");
      css.textContent =
        "#ourea-routes{position:fixed;top:0;right:0;bottom:0;width:min(1100px,100vw);z-index:50;background:#04080a;" +
        "box-shadow:-20px 0 60px rgba(0,0,0,.55);transform:translateX(100%);transition:transform .35s cubic-bezier(.2,.8,.2,1)}" +
        "#ourea-routes.open{transform:none}#ourea-routes iframe{border:0;width:100%;height:100%;display:block}" +
        "#ourea-routes button{position:absolute;top:max(12px,env(safe-area-inset-top,0px));right:max(12px,env(safe-area-inset-right,0px));z-index:2;" +
        "font:600 12px 'Chakra Petch',Arial,sans-serif;letter-spacing:.08em;color:#eef5f3;background:rgba(8,14,17,.92);" +
        "border:1px solid rgba(200,240,240,.16);border-radius:9px;padding:9px 14px;cursor:pointer}" +
        "#ourea-routes button:hover{background:#13222a}@media (prefers-reduced-motion:reduce){#ourea-routes{transition:none}}";
      document.head.appendChild(css);
      p = document.createElement("div"); p.id = "ourea-routes"; p.setAttribute("role", "dialog"); p.setAttribute("aria-label", "Routes to school and day care");
      var f = document.createElement("iframe"); f.title = "Routes to school and day care"; f.allow = "clipboard-write; web-share";
      var b = document.createElement("button"); b.textContent = "BACK TO 3D MAP  ×";
      var close = function () { p.classList.remove("open"); var c = document.querySelector("canvas"); if (c) c.focus(); };
      b.onclick = close;
      document.addEventListener("keydown", function (e) { if (e.key === "Escape" && p.classList.contains("open")) close(); });
      window.addEventListener("message", function (e) { if (e.origin === location.origin && e.data === "ourea-close-routes") close(); });
      p.appendChild(f); p.appendChild(b); document.body.appendChild(p);
    }
    var frame = p.querySelector("iframe");
    var want = url + (url.indexOf("?") < 0 ? "?" : "&") + "embed=1";
    if (frame.dataset.src !== want) { frame.dataset.src = want; frame.src = want; }
    requestAnimationFrame(function () { p.classList.add("open"); });
  },

  // Share a message: the phone's share sheet (Zalo, Messages, ...) where there is one, otherwise copy it
  // and open Zalo on the web to paste it in. Returns 1 if the share sheet opened, 0 if it copied.
  OureaShare: function (ptr) {
    var text = UTF8ToString(ptr);
    if (navigator.share) { navigator.share({ text: text }).catch(function () {}); return 1; }
    try { navigator.clipboard.writeText(text); } catch (e) {}
    var w = null;
    try { w = window.open("https://chat.zalo.me/", "_blank"); } catch (e) {}
    if (w) { try { w.opener = null; } catch (e) {} }
    return 0;
  }
});
