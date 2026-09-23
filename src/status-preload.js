const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('taskbarStatus', {
  onUpdate: (listener) => ipcRenderer.on('status:update', (_event, state) => listener(state)),
  reportContentWidth: (width) => ipcRenderer.send('status:content-width', width)
})
