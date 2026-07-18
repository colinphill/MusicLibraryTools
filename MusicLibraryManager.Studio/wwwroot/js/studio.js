window.studio = {
    focus: function (id) {
        const element = document.getElementById(id);
        if (element) { element.focus(); element.select?.(); }
    },
    scrollIntoView: function (id) {
        document.getElementById(id)?.scrollIntoView({ block: "nearest" });
    },
    installDropBridge: function (elementId) {
        const element = document.getElementById(elementId);
        if (!element || element.dataset.dropBridge === "true") return;
        element.dataset.dropBridge = "true";
        element.addEventListener("dragover", e => { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; });
        element.addEventListener("drop", async e => {
            e.preventDefault();
            if (!window.chrome?.webview || !e.dataTransfer) return;
            const handles = [];
            for (const item of e.dataTransfer.items || []) {
                if (item.getAsFileSystemHandle) {
                    const handle = await item.getAsFileSystemHandle();
                    if (handle) handles.push(handle);
                }
            }
            const objects = handles.length ? handles : Array.from(e.dataTransfer.files || []);
            if (objects.length)
                window.chrome.webview.postMessageWithAdditionalObjects("StudioFilesDropped", objects);
        });
    },
    initializeGrid: function (gridId, dotnet) {
        const grid = document.getElementById(gridId);
        if (!grid) return;
        for (const resizer of grid.querySelectorAll(".column-resizer")) {
            if (resizer.dataset.resizeReady === "true") continue;
            resizer.dataset.resizeReady = "true";
            resizer.addEventListener("pointerdown", event => {
                event.preventDefault();
                event.stopPropagation();
                const key = resizer.dataset.columnKey;
                const startX = event.clientX;
                const startWidth = resizer.parentElement.getBoundingClientRect().width;
                resizer.setPointerCapture?.(event.pointerId);
                let frame = 0;
                let latestWidth = startWidth;
                const move = current => {
                    latestWidth = Math.max(48, startWidth + current.clientX - startX);
                    if (frame) return;
                    frame = requestAnimationFrame(() => {
                        frame = 0;
                        dotnet.invokeMethodAsync("ResizeColumn", key, latestWidth).catch(() => {});
                    });
                };
                const up = async current => {
                    window.removeEventListener("pointermove", move);
                    window.removeEventListener("pointerup", up);
                    latestWidth = Math.max(48, startWidth + current.clientX - startX);
                    if (frame) cancelAnimationFrame(frame);
                    try {
                        await dotnet.invokeMethodAsync("ResizeColumn", key, latestWidth);
                        await dotnet.invokeMethodAsync("CommitGridLayout");
                    } catch { }
                };
                window.addEventListener("pointermove", move);
                window.addEventListener("pointerup", up, { once: true });
            });
        }
    },
    initializeSplit: function (splitId, dotnet, minRightWidth) {
        const split = document.getElementById(splitId);
        const left = split?.querySelector(":scope > .split-pane-left");
        if (!split || !left) return;
        const available = Math.max(0, split.getBoundingClientRect().width - minRightWidth - 10);
        const width = Math.min(left.getBoundingClientRect().width, available);
        dotnet.invokeMethodAsync("InitializeSplit", width).catch(() => {});
    },
    beginSplitResize: function (dotnet, splitId, startX, minRightWidth) {
        const split = document.getElementById(splitId);
        const left = split?.querySelector(":scope > .split-pane-left");
        if (!split || !left) return;
        const startWidth = left.getBoundingClientRect().width;
        const maximum = Math.max(0, split.getBoundingClientRect().width - minRightWidth - 10);
        let frame = 0;
        let latestWidth = startWidth;
        const move = event => {
            latestWidth = Math.min(maximum, startWidth + event.clientX - startX);
            if (frame) return;
            frame = requestAnimationFrame(() => {
                frame = 0;
                dotnet.invokeMethodAsync("ResizeSplit", latestWidth).catch(() => {});
            });
        };
        const up = async event => {
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            latestWidth = Math.min(maximum, startWidth + event.clientX - startX);
            if (frame) cancelAnimationFrame(frame);
            try {
                await dotnet.invokeMethodAsync("ResizeSplit", latestWidth);
                await dotnet.invokeMethodAsync("CommitSplitWidth");
            } catch { }
        };
        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up, { once: true });
    }
};
