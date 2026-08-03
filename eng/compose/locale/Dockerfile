FROM postgres:18-bookworm

RUN sed -i \
      -e 's/^# en_GB.UTF-8 UTF-8/en_GB.UTF-8 UTF-8/' \
      -e 's/^# de_DE.UTF-8 UTF-8/de_DE.UTF-8 UTF-8/' \
      /etc/locale.gen \
    && locale-gen
