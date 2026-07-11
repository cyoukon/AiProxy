// ─── AI Proxy Admin - Stats ────────────────────────────────────────────────────
// Depends on: core.js (api, escapeHtml, fromLocalInput, restoreActiveTab),
//             i18n.js (t()), chart.js (Chart).
// Note: restoreActiveTab() is called at the end of this file (the last loaded
// module) so that all data-loading functions (loadOverview/loadLogs/loadStats/
// loadConfigManagement) defined in earlier modules are available.

// ─── Statistics ─────────────────────────────────────────────────────────────
let statsChart = null;
async function loadStats() {
  const params = new URLSearchParams();
  params.set('dimension', $('#statsDimension').value);
  params.set('granularity', $('#statsGranularity').value);
  const from = fromLocalInput($('#statsFrom').value);
  const to = fromLocalInput($('#statsTo').value);
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  const service = $('#statsService').value;
  if (service) params.set('service', service);
  const model = $('#statsModel').value.trim();
  if (model) params.set('model', model);

  const resp = await api('/api/stats?' + params.toString());
  if (!resp.ok) { $('#statsTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${t('stats.loadFailed')} (${resp.status})</td></tr>`; return; }
  const data = resp.body || {};
  const groups = data.groups || [];
  const series = data.series || null;
  const dimension = data.dimension === 1 ? 'model' : 'service';
  const isDaily = data.granularity === 0;

  if (groups.length === 0) {
    $('#statsTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${t('stats.noData')}</td></tr>`;
  } else {
    $('#statsTable tbody').innerHTML = groups.map(g => `
      <tr>
        <td>${escapeHtml(dimension === 'service' ? (g.serviceName || g.key) : g.key)}</td>
        <td>${g.totalCalls}</td><td>${g.successCount}</td><td>${g.failureCount}</td>
        <td>${g.promptTokensTotal}</td><td>${g.completionTokensTotal}</td><td>${g.totalTokensTotal}</td>
        <td>${g.avgDurationMs}</td>
      </tr>`).join('');
  }

  const metric = $('#statsMetric').value;
  const metricLabel = $('#statsMetric').options[$('#statsMetric').selectedIndex].text;
  const ctx = $('#statsChart').getContext('2d');
  if (statsChart) { statsChart.destroy(); statsChart = null; }
  if (typeof Chart === 'undefined') return;

  if (isDaily && series && series.length > 0) {
    const allDates = Array.from(new Set(series.flatMap(s => s.buckets.map(b => b.date)))).sort();
    const labels = allDates.map(d => new Date(d).toISOString().slice(0, 10));
    const palette = ['#2563eb','#16a34a','#d97706','#dc2626','#7c3aed','#0891b2','#db2777','#65a30d'];
    const datasets = series.map((s, i) => ({
      label: s.key,
      data: allDates.map(d => { const b = s.buckets.find(x => x.date === d); return b ? b[metric] : 0; }),
      borderColor: palette[i % palette.length],
      backgroundColor: palette[i % palette.length] + '33',
      tension: 0.25, fill: false
    }));
    statsChart = new Chart(ctx, { type: 'line', data: { labels, datasets }, options: { responsive: true, maintainAspectRatio: false, plugins: { title: { display: true, text: t('stats.chartDaily', { metric: metricLabel }) } }, scales: { y: { beginAtZero: true } } } });
  } else {
    const labels = groups.map(g => dimension === 'service' ? (g.serviceName || g.key) : g.key);
    const values = groups.map(g => g[metric]);
    statsChart = new Chart(ctx, { type: 'bar', data: { labels, datasets: [{ label: metricLabel, data: values, backgroundColor: '#2563eb88', borderColor: '#2563eb', borderWidth: 1 }] }, options: { responsive: true, maintainAspectRatio: false, plugins: { title: { display: true, text: t('stats.chartCumulative', { metric: metricLabel }) } }, scales: { y: { beginAtZero: true } } } });
  }
}
$('#statsSearch').addEventListener('click', () => loadStats());

// ─── Startup ────────────────────────────────────────────────────────────────
// 恢复上次激活的页签并加载对应数据（默认 overview → loadOverview）
// 此调用必须在所有模块加载完成后执行（restoreActiveTab → refreshActiveTab 会调用
// loadOverview/loadLogs/loadStats/loadConfigManagement，它们定义在更早加载的模块中），
// 故放在最后一个加载的模块文件末尾。
restoreActiveTab();
