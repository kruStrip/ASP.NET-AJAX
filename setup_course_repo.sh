#!/usr/bin/env bash
set -euo pipefail

# Запустить в корне локального клона github.com/kruStrip/ASP.NET-AJAX
if [[ ! -d .git ]]; then
  echo 'Ошибка: запустите скрипт из корня клонированного репозитория ASP.NET-AJAX.' >&2
  exit 1
fi
remote="$(git remote get-url origin 2>/dev/null || true)"
if [[ "$remote" != *"kruStrip/ASP.NET-AJAX"* ]]; then
  echo "Ошибка: Git remote origin ($remote) не похож на kruStrip/ASP.NET-AJAX." >&2
  exit 1
fi
base='4_курс/Разработка_веб_приложений_ASP.NET_AJAX'
if [[ ! -f "$base/README.md" ]]; then
  echo 'Ошибка: сначала распакуйте архив в корень репозитория.' >&2
  exit 1
fi

copy_project() {
  local from="$1"
  local to="$2"
  if [[ ! -d "$from" ]]; then
    echo "Пропускаю отсутствующий проект: $from" >&2
    return
  fi
  mkdir -p "$to"
  # Перед копированием проверяем, что в месте назначения нет исходных файлов.
  # Повторный запуск не затирает уже изменённый код.
  if find "$to" -mindepth 1 -maxdepth 1 -type f ! -name '.gitkeep' -print -quit | grep -q . || \
     find "$to" -mindepth 1 -maxdepth 1 -type d -print -quit | grep -q .; then
      echo "Существующий проект не перезаписан: $to"
      return
  fi
  cp -a "$from"/. "$to"/
  echo "Скопировано: $from -> $to"
}

copy_project 'TaskBoard' "$base/Архитектура_ASP.Net_Core/проект"
copy_project 'DashboardAdmin' "$base/Razor_Views_и_клиентская_часть/ТЗ1/проект"
copy_project 'TaskBoard' "$base/Razor_Views_и_клиентская_часть/ТЗ2/проект"
copy_project 'TaskBoard' "$base/Razor_Views_и_клиентская_часть/ТЗ3/проект"
copy_project 'TaskFlowApi' "$base/Rest_API_в_ASP.NET_Core/проект"
copy_project 'LiveMonitor' "$base/SignalR_и_real-time/проект"
copy_project 'SecureTodo' "$base/Безопасность_аутентификация_авторизация/проект"

if ! grep -Fq '[Материалы по структуре преподавателя]' README.md 2>/dev/null; then
cat >> README.md <<'MARKDOWN'

## 4-й курс — структура сдачи

[Материалы по структуре преподавателя](4_курс/Разработка_веб_приложений_ASP.NET_AJAX/README.md)

Практические работы, лекции, методички и каркас итогового проекта «МузЛото» распределены по разделам. Исходные папки существующих проектов сохранены. Итоговый проект пока находится на этапе проектирования.
MARKDOWN
fi

printf '\nСтруктура готова локально. Изменения:\n'
git status --short
printf '\nДля отправки на GitHub выполните:\n'
printf "git add README.md '%s'\n" "$base"
printf 'git commit -m "docs: organize ASP.NET course materials"\n'
printf 'git push origin main\n'
