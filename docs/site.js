(() => {
  document.documentElement.classList.add('has-js');

  const header = document.querySelector('#site-header');
  const menuButton = document.querySelector('#menu-toggle');
  const navigation = document.querySelector('#site-nav');
  const menuLabel = menuButton?.querySelector('.sr-only');

  const updateHeader = () => {
    header?.classList.toggle('is-scrolled', window.scrollY > 12);
  };

  const setMenuState = (isOpen) => {
    if (!menuButton || !navigation) return;

    menuButton.setAttribute('aria-expanded', String(isOpen));
    navigation.classList.toggle('is-open', isOpen);
    document.body.classList.toggle('menu-open', isOpen);

    if (menuLabel) {
      menuLabel.textContent = isOpen ? 'Close navigation' : 'Open navigation';
    }
  };

  updateHeader();
  window.addEventListener('scroll', updateHeader, { passive: true });

  menuButton?.addEventListener('click', () => {
    setMenuState(menuButton.getAttribute('aria-expanded') !== 'true');
  });

  navigation?.addEventListener('click', (event) => {
    if (event.target.closest('a')) setMenuState(false);
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && menuButton?.getAttribute('aria-expanded') === 'true') {
      setMenuState(false);
      menuButton.focus();
    }
  });

  const desktopQuery = window.matchMedia('(min-width: 981px)');
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
})();
