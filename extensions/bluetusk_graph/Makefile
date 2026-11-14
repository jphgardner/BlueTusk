EXTENSION = bluetusk_graph
MODULE_big = bluetusk_graph
OBJS = bluetusk_graph.o
DATA = bluetusk_graph--1.0.sql bluetusk_graph--1.0--1.1.sql

PG_CONFIG ?= pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
