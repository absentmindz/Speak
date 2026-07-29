(() => {
  document.documentElement.classList.add('has-motion');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const header = document.querySelector('#site-header');

  const updateHeader = () => {
    header?.classList.toggle('is-scrolled', window.scrollY > 18);
  };
  updateHeader();
  window.addEventListener('scroll', updateHeader, { passive: true });

  const revealItems = document.querySelectorAll('[data-reveal]');
  if (reducedMotion || !('IntersectionObserver' in window)) {
    revealItems.forEach((item) => item.classList.add('is-visible'));
  } else {
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          observer.unobserve(entry.target);
        }
      });
    }, { threshold: 0.16, rootMargin: '0px 0px -36px' });
    revealItems.forEach((item) => observer.observe(item));
  }

  if (reducedMotion) return;

  document.querySelectorAll('[data-tilt]').forEach((scene) => {
    const reset = () => {
      scene.style.setProperty('--tilt-x', '0deg');
      scene.style.setProperty('--tilt-y', '0deg');
      scene.classList.remove('is-tilting');
    };

    scene.addEventListener('pointermove', (event) => {
      const bounds = scene.getBoundingClientRect();
      const x = (event.clientX - bounds.left) / bounds.width - 0.5;
      const y = (event.clientY - bounds.top) / bounds.height - 0.5;
      scene.classList.add('is-tilting');
      scene.style.setProperty('--tilt-x', `${(-y * 5).toFixed(2)}deg`);
      scene.style.setProperty('--tilt-y', `${(x * 7).toFixed(2)}deg`);
    });

    scene.addEventListener('pointerleave', reset);
    scene.addEventListener('blur', reset);
  });
})();
