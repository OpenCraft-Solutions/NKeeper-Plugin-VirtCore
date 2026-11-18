/// <reference types="novnc__novnc" />

// noVNC 1.7 exports RFB from its root; its DefinitelyTyped types still name the old path.
declare module '@novnc/novnc' {
  import RFB from '@novnc/novnc/lib/rfb'
  export default RFB
}
