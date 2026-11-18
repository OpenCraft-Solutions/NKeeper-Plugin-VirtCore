import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { GuestsView } from './GuestsView'
import { virtStyles } from './styles'

export type { GuestsViewProps, HypervisorInfo } from './GuestsView'

/**
 * virt-core's frontend (docs/architecture.md §7.36): it draws no tab of its own, and shares the guests tab with the
 * hypervisor plugins that depend on it (SDK 1.8), which wrap it in their session types' views.
 */
const plugin = definePlugin({
  id: 'virt-core',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(virtStyles)
  },
  exports: { GuestsView },
})

export default plugin
