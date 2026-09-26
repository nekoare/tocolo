using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("dev.nekoare.click-recolor.tests")]
// 有料版（dev.nekoare.click-recolor-pro）は本体の internal API を使う
[assembly: InternalsVisibleTo("dev.nekoare.click-recolor.pro.editor")]
[assembly: InternalsVisibleTo("dev.nekoare.click-recolor.pro.tests")]
