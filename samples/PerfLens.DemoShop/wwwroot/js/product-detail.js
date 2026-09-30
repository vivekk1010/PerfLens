(function () {
  'use strict';
  var id = document.getElementById('product').getAttribute('data-id');
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }
  function json(url) { return fetch(url).then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); }); }

  json('/api/products/' + id).then(function (p) {
    document.getElementById('product-meta').textContent = p.category + ' · $' + Number(p.price).toFixed(2) + ' · ★ ' + p.rating;
    document.getElementById('product-description').textContent = p.description;
  }).catch(function () { document.getElementById('product-meta').textContent = 'Details unavailable.'; });

  json('/api/products/' + id + '/reviews').then(function (reviews) {
    document.getElementById('reviews').innerHTML = reviews.map(function (r) {
      return '<li><strong>' + esc(r.author) + '</strong> ' + '★'.repeat(r.stars) + ' <span class="muted">' + esc(r.date) + '</span><br>' + esc(r.text) + '</li>';
    }).join('');
  }).catch(function () { document.getElementById('reviews').innerHTML = '<li class="muted">Reviews unavailable.</li>'; });
})();
