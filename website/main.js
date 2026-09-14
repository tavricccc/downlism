/* Downlism — 產品介紹網站
   三站共用：navHairline()、revealOnScroll()
   Downlism 專屬：segmentedBar()（兄弟站把這一個函式整組換成自己的母題）        */
(function () {
  'use strict';

  var reduced = window.matchMedia('(prefers-reduced-motion: reduce)');

  /* ---------------------------------------------------------------- 共用 */

  /** 捲過 hero 之後才給導覽列下緣分隔線。 */
  function navHairline() {
    var nav = document.getElementById('nav');
    var hero = document.querySelector('.hero');
    if (!nav || !hero || !('IntersectionObserver' in window)) return;

    new IntersectionObserver(function (entries) {
      nav.classList.toggle('is-stuck', !entries[0].isIntersecting);
    }, { rootMargin: '-100% 0px 0px 0px' }).observe(hero);
  }

  /** 捲動揭示。初始狀態在 HTML 裡就是可見的，位移只在 JS 接手後才加上。 */
  function revealOnScroll() {
    if (reduced.matches || !('IntersectionObserver' in window)) return;

    var targets = document.querySelectorAll(
      '.sec-head, .motif-head, .motif, .motif-notes > *, .shot-pair figure,' +
      '.diagram-wrap, .prose-cols > *, .split-copy, .split-shot,' +
      '.decisions > div, .table-scroll, .notes-pair > *, .install-notes,' +
      '.cta-strip, .fam-card'
    );
    if (!targets.length) return;

    document.documentElement.classList.add('js-reveal');
    Array.prototype.forEach.call(targets, function (el) { el.classList.add('reveal'); });

    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-in');
        observer.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.08 });

    Array.prototype.forEach.call(targets, function (el) { observer.observe(el); });
  }

  /* ------------------------------------------------- Downlism 的那一個時刻 */

  /**
   * 分段進度條：十六段，每段從自己的起點往前填、速度不同，第十段明顯落後。
   * 捲到才開始跑；離開畫面就停。prefers-reduced-motion 時保留 HTML 裡的靜止中途狀態。
   */
  function segmentedBar() {
    var bar = document.getElementById('segbar');
    var totalOut = document.getElementById('motif-total');
    var lagOut = document.getElementById('motif-lag');
    if (!bar || !totalOut || !lagOut) return;

    var segments = Array.prototype.slice.call(bar.querySelectorAll('.seg'));
    if (!segments.length || reduced.matches || !('IntersectionObserver' in window)) return;

    var LAG = 9;                 /* 落後的那一段 */
    var HOLD = 1600;             /* 全部填滿後停留多久再重來 */
    var rates = segments.map(function (seg, i) {
      if (i === LAG) return 0.052;
      /* 固定的偽亂數：每次載入都一樣，但十六段彼此不同。 */
      return 0.155 + ((i * 37 + 11) % 23) / 23 * 0.135;
    });
    var offsets = segments.map(function (seg, i) {
      return i === LAG ? 0 : ((i * 53 + 7) % 19) / 19 * 0.55;
    });

    var fills = segments.map(function (seg) { return seg.querySelector('.seg-fill'); });
    var start = 0, raf = 0, running = false, doneAt = 0;
    var lastTotal = -1, lastLag = -1;

    function paint(now) {
      raf = 0;
      if (!running) return;

      var t = (now - start) / 1000;
      var sum = 0, lagValue = 0, all = true;

      for (var i = 0; i < segments.length; i++) {
        var p = Math.min(1, Math.max(0, (t - offsets[i]) * rates[i]));
        sum += p;
        if (i === LAG) lagValue = p;
        if (p < 1) all = false;
        fills[i].style.width = (p * 100).toFixed(2) + '%';
      }

      var total = Math.round(sum / segments.length * 100);
      if (total !== lastTotal) { totalOut.textContent = total + '%'; lastTotal = total; }
      var lagPercent = Math.round(lagValue * 100);
      if (lagPercent !== lastLag) {
        lagOut.textContent = '最慢的一段：' + lagPercent + '%';
        lastLag = lagPercent;
      }

      if (all) {
        if (!doneAt) doneAt = now;
        if (now - doneAt > HOLD) { doneAt = 0; start = now; }
      }
      raf = requestAnimationFrame(paint);
    }

    function play() {
      if (running) return;
      running = true;
      start = performance.now();
      doneAt = 0;
      raf = requestAnimationFrame(paint);
    }

    function pause() {
      running = false;
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
    }

    var inView = false;
    new IntersectionObserver(function (entries) {
      inView = entries[0].isIntersecting;
      if (inView && !document.hidden) play(); else pause();
    }, { threshold: 0.35 }).observe(bar);

    document.addEventListener('visibilitychange', function () {
      if (document.hidden) pause(); else if (inView) play();
    });
  }

  navHairline();
  revealOnScroll();
  segmentedBar();
})();
