import re, os

fixes = {
    'Biamp_Tesira.usp': {
        'add_var': '''STRING device_ip$[32];
INTEGER _selOutN;            // 当前选中的输出（切回矩阵页时同步给 C#）''',
        'add_var_old': 'STRING device_ip$[32];',
        'push_out': '''PUSH Matrx_OUT
{
    INTEGER n;
    n = GetLastModifiedArrayIndex();
    _selOutN = n;
    matrix.SelectOutput(n);
}
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.ToggleRoute(n); }''',
        'push_out_old': '''PUSH Matrx_OUT { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.SelectOutput(n); }
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.ToggleRoute(n); }''',
        'change_block_old': '''// ---------------- 页面打开时刷新电平/静音 ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    INTEGER found;
    found = 0;
    IF(mixpage_fb)
    {
        // 进入矩阵页：先同步"当前高亮的输出"给 C#（VTP 自己记住了高亮状态，C# 不知道）
        // 扫描 MatrxOUTFb 数组，找到高亮的那路（=1 的那路），调 SelectOutput 让 C# 同步
        FOR (n = 1 TO CH)
        {
            IF (found = 0)
            {
                IF (MatrxOUTFb[n])
                {
                    matrix.SelectOutput(n);   // C# 同步选中 + 读该输出路由
                    found = 1;
                }
            }
        }
        // 没有高亮（首次进入）：默认输出 1
        IF (found = 0)
        {
            matrix.SelectOutput(1);
        }
    }
}''',
        'change_block_new': '''// ---------------- 页面打开时刷新电平/静音 ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    IF(mixpage_fb)
    {
        // SIMPL+ 里 DIGITAL_OUTPUT 不可读，无法扫描 MatrxOUTFb 当前值。
        // 改用 _selOutN 全局变量追踪（PUSH Matrx_OUT 时记录），进入页面同步给 C#。
        IF (_selOutN = 0)
        {
            matrix.SelectOutput(1);
        }
        ELSE
        {
            matrix.SelectOutput(_selOutN);
        }
    }
}''',
    },
    'AudioMatrix_StageCraft.usp': {
        'add_var': '''STRING device_ip$[32];
INTEGER _selOutN;            // 当前选中的输出（切回矩阵页时同步给 C#）''',
        'add_var_old': 'STRING device_ip$[32];',
        'push_out': '''PUSH Matrx_OUT
{
    INTEGER n;
    n = GetLastModifiedArrayIndex();
    _selOutN = n;
    matrix.SelectOutput(n);
}
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.ToggleRoute(n); }''',
        'push_out_old': '''PUSH Matrx_OUT { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.SelectOutput(n); }
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); matrix.ToggleRoute(n); }''',
        'change_block_old': '''// ---------------- 页面驱动轮询（进入页面启动，离开停止） ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    INTEGER found;
    found = 0;
    IF(mixpage_fb)
    {
        // 进入矩阵页：先同步"当前高亮的输出"给 C#（VTP 自己记住了高亮状态，C# 不知道）
        // 扫描 MatrxOUTFb 数组，找到高亮的那路（=1 的那路），调 SelectOutput 让 C# 同步
        FOR (n = 1 TO CH)
        {
            IF (found = 0)
            {
                IF (MatrxOUTFb[n])
                {
                    matrix.SelectOutput(n);   // C# 同步选中 + 读该输出路由
                    found = 1;
                }
            }
        }
        // 没有高亮（首次进入）：默认输出 1
        IF (found = 0)
        {
            matrix.SelectOutput(1);
        }
        matrix.SetPollMode(3);   // 混音页：路由 + 当前输出电平/meter
    }
    ELSE
    {
        matrix.SetPollMode(0);   // 离开页面：停止轮询
    }
}''',
        'change_block_new': '''// ---------------- 页面驱动轮询（进入页面启动，离开停止） ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    IF(mixpage_fb)
    {
        // SIMPL+ 里 DIGITAL_OUTPUT 不可读，无法扫描 MatrxOUTFb 当前值。
        // 改用 _selOutN 全局变量追踪（PUSH Matrx_OUT 时记录），进入页面同步给 C#。
        IF (_selOutN = 0)
        {
            matrix.SelectOutput(1);
        }
        ELSE
        {
            matrix.SelectOutput(_selOutN);
        }
        matrix.SetPollMode(3);   // 混音页：路由 + 当前输出电平/meter
    }
    ELSE
    {
        matrix.SetPollMode(0);   // 离开页面：停止轮询
    }
}''',
    },
    'Redundant_AudioMatrix_StageCraft.usp': {
        'add_var': '''STRING device_ip$[32];
INTEGER _selOutN;            // 当前选中的输出（切回矩阵页时同步给 C#）''',
        'add_var_old': 'STRING device_ip$[32];',
        'push_out': '''PUSH Matrx_OUT
{
    INTEGER n;
    n = GetLastModifiedArrayIndex();
    _selOutN = n;
    rmatrix.SelectOutput(n);
}
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); rmatrix.ToggleRoute(n); }''',
        'push_out_old': '''PUSH Matrx_OUT { INTEGER n; n = GetLastModifiedArrayIndex(); rmatrix.SelectOutput(n); }
PUSH Matrx_IN  { INTEGER n; n = GetLastModifiedArrayIndex(); rmatrix.ToggleRoute(n); }''',
        'change_block_old': '''// ---------------- 页面驱动轮询（进入页面启动，离开停止） ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    INTEGER found;
    found = 0;
    IF(mixpage_fb)
    {
        // 进入矩阵页：先同步"当前高亮的输出"给 C#（VTP 自己记住了高亮状态，C# 不知道）
        // 扫描 MatrxOUTFb 数组，找到高亮的那路（=1 的那路），调 SelectOutput 让 C# 同步
        FOR (n = 1 TO CH)
        {
            IF (found = 0)
            {
                IF (MatrxOUTFb[n])
                {
                    rmatrix.SelectOutput(n);   // C# 同步选中 + 读该输出路由
                    found = 1;
                }
            }
        }
        // 没有高亮（首次进入）：默认输出 1
        IF (found = 0)
        {
            rmatrix.SelectOutput(1);
        }
        rmatrix.SetPollMode(3);   // 混音页：路由 + 当前输出电平/meter
    }
    ELSE
    {
        rmatrix.SetPollMode(0);   // 离开页面：停止轮询
    }
}''',
        'change_block_new': '''// ---------------- 页面驱动轮询（进入页面启动，离开停止） ----------------
CHANGE mixpage_fb
{
    INTEGER n;
    IF(mixpage_fb)
    {
        // SIMPL+ 里 DIGITAL_OUTPUT 不可读，无法扫描 MatrxOUTFb 当前值。
        // 改用 _selOutN 全局变量追踪（PUSH Matrx_OUT 时记录），进入页面同步给 C#。
        IF (_selOutN = 0)
        {
            rmatrix.SelectOutput(1);
        }
        ELSE
        {
            rmatrix.SelectOutput(_selOutN);
        }
        rmatrix.SetPollMode(3);   // 混音页：路由 + 当前输出电平/meter
    }
    ELSE
    {
        rmatrix.SetPollMode(0);   // 离开页面：停止轮询
    }
}''',
    },
}

for fname, fix in fixes.items():
    path = os.path.join(r'C:\Users\YSL\Desktop\cp4', fname)
    content = open(path, encoding='utf-8').read()

    if fix['add_var_old'] in content and '_selOutN' not in content:
        content = content.replace(fix['add_var_old'], fix['add_var'], 1)

    if fix['push_out_old'] in content:
        content = content.replace(fix['push_out_old'], fix['push_out'], 1)

    if fix['change_block_old'] in content:
        content = content.replace(fix['change_block_old'], fix['change_block_new'], 1)

    open(path, 'w', encoding='utf-8', newline='').write(content)
    o, c = content.count('{'), content.count('}')
    print(f'{fname}: 花括号 {o}/{c} ' + ('OK' if o==c else 'FAIL'))