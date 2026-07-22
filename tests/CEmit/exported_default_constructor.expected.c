// file: exported_default_constructor.c
#include "exported_default_constructor_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
static _ExportedAbstract _ExportedAbstract__vt;

static _ExportedAbstract _ExportedAbstract__vt = { .use = NULL };
void ExportedConcrete_op_initnew(ExportedConcrete *this)
{
}

ExportedConcrete *ExportedConcrete_create(void)
{
	ExportedConcrete *_created0 = (ExportedConcrete *)(malloc(sizeof(ExportedConcrete)));
	if ((_created0 != NULL))
	{
		*_created0 = (ExportedConcrete){0};
		ExportedConcrete_op_initnew(_created0);
	}
	return _created0;
}

void ExportedConcrete_destroy(ExportedConcrete *this)
{
	free((void *)(this));
}

void ExportedAbstract_use(ExportedAbstract *this)
{
	this->_vt->use(this);
}

// file: exported_default_constructor.h
#ifndef EXPORTED_DEFAULT_CONSTRUCTOR_H_
#define EXPORTED_DEFAULT_CONSTRUCTOR_H_

#include "exported_default_constructor_private.h"

void ExportedConcrete_op_initnew(ExportedConcrete *this);
ExportedConcrete *ExportedConcrete_create(void);
void ExportedConcrete_destroy(ExportedConcrete *this);
void ExportedAbstract_use(ExportedAbstract *this);

#endif
// file: exported_default_constructor_private.h
#ifndef EXPORTED_DEFAULT_CONSTRUCTOR_PRIVATE_H_
#define EXPORTED_DEFAULT_CONSTRUCTOR_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct ExportedConcrete ExportedConcrete;
typedef struct ExportedAbstract ExportedAbstract;
typedef struct _ExportedAbstract _ExportedAbstract;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct _ExportedAbstract
{
	void (* use)(ExportedAbstract *ctx);
};
struct ExportedConcrete
{
	int value;
};
struct ExportedAbstract
{
	_ExportedAbstract *_vt;
};

/* Function declarations. */
void ExportedConcrete_op_initnew(ExportedConcrete *this);
ExportedConcrete *ExportedConcrete_create(void);
void ExportedConcrete_destroy(ExportedConcrete *this);
void ExportedAbstract_use(ExportedAbstract *this);

/* Object declarations. */


#endif
