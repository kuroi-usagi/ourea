// Open a page in a new tab from a Unity click. Unity handles clicks a frame after the browser does;
// browsers still allow window.open for a few seconds after a click, so try at once, and if the
// pop-up was blocked, open it on the next pointer release instead (a fresh user gesture).
mergeInto(LibraryManager.library, {
  OureaOpenOnRelease: function (ptr) {
    var url = new URL(UTF8ToString(ptr), window.location.href).href;
    var w = null;
    try { w = window.open(url, "_blank"); } catch (e) {}
    if (w) { try { w.opener = null; } catch (e) {} return; }
    var open = function () {
      document.removeEventListener("pointerup", open, true);
      document.removeEventListener("touchend", open, true);
      window.open(url, "_blank");
    };
    document.addEventListener("pointerup", open, true);
    document.addEventListener("touchend", open, true);
  }
});
