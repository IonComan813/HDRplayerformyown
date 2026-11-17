"""
PyInstaller 入口：把 ffsubsync（https://github.com/smacke/ffsubsync）打包成 tools\\ffsubsync\\ffsubsync.exe。
由 .github/workflows/release.yml 调用；HdrPlayer 的右键菜单 → 字幕 → 自动同步字幕 会用到它。
"""
import multiprocessing
import sys

from ffsubsync import main

if __name__ == "__main__":
    # ffsubsync 的多段并行抽取会用到 multiprocessing，冻结成 exe 后在 Windows 上需要这一句
    multiprocessing.freeze_support()
    sys.exit(main())
