(function () {
  'use strict';
  var grid = document.getElementById('product-grid');
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }

  fetch('/api/products')
    .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); })
    .then(function (items) {
      grid.innerHTML = items.map(function (p) {
        return '<a class="card product" href="/products/' + p.id + '">' +
          '<img src="' + esc(p.image) + '" width="160" height="120" alt="' + esc(p.name) + '">' +
          '<h3>' + esc(p.name) + '</h3>' +
          '<p class="price">$' + Number(p.price).toFixed(2) + '</p></a>';
      }).join('');
      grid.setAttribute('aria-busy', 'false');
    })
    .catch(function () { grid.innerHTML = '<p class="alert">Could not load products.</p>'; });
})();
