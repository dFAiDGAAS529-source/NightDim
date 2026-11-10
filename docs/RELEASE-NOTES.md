# NightDim 1.1.2

## Русский

- Убрано принудительное исключение окна затемнения из захвата Windows в обоих режимах. При записи всего дисплея затемнение может попасть в кадр.
- Добавлен режим **«Без экранного слоя»** для совместимых SDR-мониторов. Он использует только гамма-кривую и даёт диапазон 43–100%.
- Слой больше не пересоздаётся при изменении геометрии или цветового режима того же дисплея.
- Нативная проверка подтверждает, что окно слоя использует стандартный режим захвата Windows и сохраняет заданную прозрачность.

Перед обновлением выберите «Выход» в меню NightDim в трее, затем запустите установщик. Профили сохранятся.

## English

- Removed forced Windows capture exclusion from the dimming overlay in both modes. Recording an entire display may include the dimming effect.
- Added **Without screen overlay** for compatible SDR displays. It uses gamma only with a 43–100% brightness range.
- The existing overlay is retained when the geometry or color mode changes on the same display.
- The native probe verifies that the layered window uses the standard Windows capture policy and retains its configured opacity.

Exit NightDim from the notification area before running the installer. Your profiles are preserved.
