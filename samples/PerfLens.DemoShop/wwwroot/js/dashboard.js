(function () {
  'use strict';
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }
  function json(url) { return fetch(url).then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); }); }

  json('/api/orders').then(function (orders) {
    document.querySelector('#orders-table tbody').innerHTML = orders.map(function (o) {
      return '<tr><td>' + esc(o.id) + '</td><td>' + esc(o.date) + '</td><td>' + esc(o.status) + '</td><td>' + o.items +
        '</td><td>$' + Number(o.total).toFixed(2) + '</td></tr>';
    }).join('');
  }).catch(function () { document.querySelector('#orders-table tbody').innerHTML = '<tr><td colspan="5">Orders unavailable.</td></tr>'; });

  json('/api/stats').then(function (stats) {
    document.querySelector('#stats-table tbody').innerHTML = Object.keys(stats).map(function (k) {
      return '<tr><th>' + esc(k) + '</th><td>' + esc(stats[k]) + '</td></tr>';
    }).join('');
  }).catch(function () { document.querySelector('#stats-table tbody').innerHTML = '<tr><td>Stats unavailable.</td></tr>'; });
})();
