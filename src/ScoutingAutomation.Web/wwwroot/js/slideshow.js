// Full screen for the slideshow stage
window.duSlideshow = {
    toggleFullscreen: function (element) {
        if (document.fullscreenElement) {
            return document.exitFullscreen();
        }
        if (element && element.requestFullscreen) {
            return element.requestFullscreen();
        }
    },
    exitFullscreen: function () {
        if (document.fullscreenElement) {
            return document.exitFullscreen();
        }
    }
};
