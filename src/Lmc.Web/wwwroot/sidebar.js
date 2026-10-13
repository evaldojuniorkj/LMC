(() => {
  const button = document.getElementById('sidebar-toggle');
  const key = 'lmc.sidebar.collapsed';
  let collapsed = window.matchMedia('(max-width: 1000px)').matches;
  try {
    const saved = localStorage.getItem(key);
    if (saved !== null) collapsed = saved === 'true';
  } catch { /* A preferência funciona também sem armazenamento disponível. */ }
  document.querySelectorAll('.sidebar .nav-item').forEach(item => {
    item.title = item.textContent.trim();
  });
  function render() {
    document.body.classList.toggle('sidebar-collapsed', collapsed);
    const label = collapsed ? 'Expandir menu' : 'Retrair menu';
    button.setAttribute('aria-expanded', String(!collapsed));
    button.setAttribute('aria-label', label);
    button.title = label;
    button.querySelector('span').textContent = label;
  }
  button.addEventListener('click', () => {
    collapsed = !collapsed;
    render();
    try { localStorage.setItem(key, String(collapsed)); } catch { }
  });
  render();
})();
