// Runs before the page paints: match the website and patcher unless a visitor chose the light theme.
function setTheme(theme) {
  theme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme;
  try { if (theme) localStorage.setItem('theme', theme); } catch { /* private window: the choice lasts for this page */ }
  const dark = theme === 'dark';
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.content = dark ? '#0d0b10' : '#f5eee7';
}
try { setTheme(localStorage.getItem('theme')); } catch { setTheme('dark'); }
