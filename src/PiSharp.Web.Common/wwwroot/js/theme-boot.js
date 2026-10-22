// PiSharp.Web.Common (decision 0011): applies the stored theme before the first paint. A classic script in <head> (see _WebCommonHead);
// dark is the default, so only a stored "light" changes anything. Its data-theme-key names the localStorage key, which it leaves on
// <html data-theme-key> for theme.js.
(function () {
  var script = document.currentScript;
  var key = (script && script.getAttribute('data-theme-key')) || 'pisharp-theme';
  var root = document.documentElement;
  root.setAttribute('data-theme-key', key);
  try {
    if (localStorage.getItem(key) === 'light') root.setAttribute('data-bs-theme', 'light');
  } catch (e) { /* storage unavailable */ }
})();
