set -euo pipefail
cd "$(dirname "$0")"
gcc --version
gcc -O2 -fPIC -shared -Wall -Wextra -o libbudget_native.so budget_native.c