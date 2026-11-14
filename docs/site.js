(() => {
  document.documentElement.classList.add('has-js');

  const revealItems = document.querySelectorAll('[data-reveal]');
  const canAnimate = 'IntersectionObserver' in window
    && !window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  if (canAnimate && revealItems.length) {
    document.documentElement.classList.add('has-motion');
  }

  const header = document.querySelector('#site-header');
  const menuButton = document.querySelector('#menu-toggle');
  const navigation = document.querySelector('#site-nav');
  const menuLabel = menuButton?.querySelector('.sr-only');
  const headerBrand = document.querySelector('.site-header .brand');

  const updateHeader = () => {
    header?.classList.toggle('is-scrolled', window.scrollY > 12);
  };

  const setMenuState = (isOpen) => {
    if (!menuButton || !navigation) return;

    menuButton.setAttribute('aria-expanded', String(isOpen));
    navigation.classList.toggle('is-open', isOpen);
    document.body.classList.toggle('menu-open', isOpen);

    if (menuLabel) {
      menuLabel.textContent = isOpen ? 'Close menu' : 'Open menu';
    }
  };

  updateHeader();
  window.addEventListener('scroll', updateHeader, { passive: true });

  menuButton?.addEventListener('click', () => {
    setMenuState(menuButton.getAttribute('aria-expanded') !== 'true');
  });

  headerBrand?.addEventListener('click', () => setMenuState(false));

  navigation?.addEventListener('click', (event) => {
    const link = event.target.closest('a');
    if (!link) return;

    const wasOpen = menuButton?.getAttribute('aria-expanded') === 'true';
    setMenuState(false);

    if (wasOpen && link.hash) {
      const target = document.querySelector(link.hash);
      if (target) {
        target.setAttribute('tabindex', '-1');
        target.focus({ preventScroll: true });
        target.addEventListener('blur', () => target.removeAttribute('tabindex'), { once: true });
      }
    }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && menuButton?.getAttribute('aria-expanded') === 'true') {
      setMenuState(false);
      menuButton.focus();
    }
  });

  const desktopQuery = window.matchMedia('(min-width: 901px)');
  const closeDesktopMenu = (event) => {
    if (event.matches) setMenuState(false);
  };

  desktopQuery.addEventListener?.('change', closeDesktopMenu);
  desktopQuery.addListener?.(closeDesktopMenu);

  const video = document.querySelector('#speak-demo');
  const videoLaunch = document.querySelector('.video-launch');

  if (video && videoLaunch) {
    const showLaunch = () => {
      videoLaunch.hidden = false;
    };

    const hideLaunch = () => {
      videoLaunch.hidden = true;
    };

    videoLaunch.addEventListener('click', async () => {
      try {
        await video.play();
      } catch {
        showLaunch();
      }
    });

    video.addEventListener('play', hideLaunch);
    video.addEventListener('playing', hideLaunch);
    video.addEventListener('ended', showLaunch);
    video.addEventListener('pause', () => {
      if (video.currentTime === 0 || video.ended) showLaunch();
    });
  }

  if (revealItems.length) {
    if (canAnimate) {
      const observer = new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          entry.target.classList.add('is-visible');
          observer.unobserve(entry.target);
        }
      }, { rootMargin: '0px 0px -8% 0px', threshold: 0.12 });

      revealItems.forEach((item) => {
        if (item.getBoundingClientRect().top < window.innerHeight * 0.92) {
          item.classList.add('is-visible');
        } else {
          observer.observe(item);
        }
      });
    } else {
      revealItems.forEach((item) => item.classList.add('is-visible'));
    }
  }
})();
