// Saves a .NET stream (DotNetStreamReference) as a file download.
window.flowledgerDownload = async (fileName, streamRef) => {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer]));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};

// Scrolls the bill sheet horizontally so the current-month column is in view.
window.flowledgerFocusCurrentMonth = () => {
    const th = document.querySelector('.fl-sheet thead th.fl-sheet-cur');
    if (th) th.scrollIntoView({ block: 'nearest', inline: 'center' });
};
