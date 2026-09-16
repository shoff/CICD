window.cicd = {
  scrollLog: function () {
    const el = document.getElementById('build-log');
    if (el) el.scrollTop = el.scrollHeight;
  }
};
