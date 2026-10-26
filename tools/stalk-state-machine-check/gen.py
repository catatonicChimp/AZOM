import os
here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, '..', '..', 'Settings', 'MozaPluginSettings.cs')
s = open(src, encoding='utf-8').read()
i = s.index('    /// <summary>\n    /// Truck-sim keyboard-mapping config')
os.makedirs(os.path.join(here, 'gen'), exist_ok=True)
open(os.path.join(here, 'gen', 'Settings.cs'), 'w', encoding='utf-8').write(
    'using System;\nusing System.Collections.Generic;\nusing MozaPlugin.Devices.StalksTruckSim;\n'
    'namespace MozaPlugin.Settings\n{\n' + s[i:])
