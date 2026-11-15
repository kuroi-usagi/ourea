// Copy text to the browser clipboard (StoryMode's message drafts). Called from a click, so the
// browser allows it; falls back to a hidden textarea where the Clipboard API is missing.
mergeInto(LibraryManager.library, {
  OureaCopy: function (ptr) {
    var text = UTF8ToString(ptr);
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).catch(function () {});
      return;
    }
    var ta = document.createElement("textarea");
    ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.select();
    try { document.execCommand("copy"); } catch (e) {}
    document.body.removeChild(ta);
  }
});
