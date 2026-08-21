import os
root = r'D:\Crestron\simpl#\4-Series\CrestronDeviceLibrary'
files = ['Devices/StageCraftMatrix.cs', 'Devices/SonyViscaCamera.cs', 'DeviceManager.cs',
         'Common/PacketBuilder.cs', 'Common/ResponseParser.cs']
for f in files:
    s = open(os.path.join(root, f), encoding='utf-8').read()
    op = s.count('{'); cl = s.count('}')
    ns = 'namespace ' in s
    print('  %-32s  {=%d }=%d  namespace=%s' % (f, op, cl, ns))
