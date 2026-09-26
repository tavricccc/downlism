(() => {
  const menu = document.querySelector(".mobile-nav");
  menu?.querySelectorAll("nav a").forEach((link) => {
    link.addEventListener("click", () => { menu.open = false; });
  });

  const visual = document.querySelector(".range-visual");
  if (visual && !window.matchMedia("(prefers-reduced-motion: reduce)").matches && "IntersectionObserver" in window) {
    const observer = new IntersectionObserver((entries) => {
      if (!entries[0].isIntersecting) return;
      visual.classList.add("is-animated");
      observer.disconnect();
    }, { threshold: 0.45 });
    observer.observe(visual);
  }
})();
