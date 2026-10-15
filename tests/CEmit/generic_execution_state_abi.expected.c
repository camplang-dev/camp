// file: generic_execution_state_abi.c
#include "generic_execution_state_abi_private.h"

/* Private file declarations. */
typedef struct Holder_across_asyncFrame Holder_across_asyncFrame;
struct Holder_across_asyncFrame
{
	int state;
	void (* complete)(void *arg0, int arg1);
	void *complete_context;
	Holder *this;
	Resumer *resumer;
	int x;
	int await0_result;
};
static void Holder_across_asyncResume(void *context);
static void Holder_across_asyncComplete0(void *context, int result);
void *malloc(uintptr_t size);
void free(void *ptr);
static uintptr_t sizeVia(uintptr_t sizeof_T);
static void copyVia(void *values, uintptr_t values_length, uintptr_t from, uintptr_t to, uintptr_t sizeof_T);
static retainedIter retained(void *items, uintptr_t sizeof_T, const char *typenameof_T, const Reader *vtableof_T_Reader);
static uintptr_t produce(const void *value, uintptr_t count, void *buffer, uintptr_t buffer_length, uintptr_t sizeof_T);
static uintptr_t prepared(const void *value, uintptr_t sizeof_T);
static void *identity(void *pointer);
static void Resumer_resumeAsync(Resumer *this, void (* continuation)(void *arg0), void *continuation_context);
static void tick(int value, Resumer *resumer, void (* complete)(void *context, int result), void *complete_context);
static void Holder_across(Holder *this, Resumer *resumer, void (* complete)(void *context, int result), void *complete_context);
static void Holder_op_initnew(Holder *this, void *slot, uintptr_t sizeof_T);
static Holder *Holder_create(void *slot, uintptr_t sizeof_T);
static bool retainedIter_next(retainedIter *this, int *current);
static void retainedIter_destroy(retainedIter *this);
static bool retainedIter_op_iter(void *ctx, int *current);
static uintptr_t sizeVia_lambda0(void *_context);
static void copyVia_lambda1(void *_context);

static void Holder_across_asyncComplete0(void *context, int result)
{
	Holder_across_asyncFrame *frame = (Holder_across_asyncFrame *)context;
	frame->await0_result = result;
	Resumer_resumeAsync(frame->resumer, Holder_across_asyncResume, frame);
}

static void Holder_across_asyncResume(void *context)
{
	Holder_across_asyncFrame *frame = (Holder_across_asyncFrame *)context;
	uint8_t *copy = __builtin_alloca(frame->this->_sizeof_T);
	switch (frame->state)
	{
		case 0: goto __async_state0;
		case 1: goto __async_state1;
	}
__async_state0: ;
	__builtin_memcpy(copy, frame->this->slot, frame->this->_sizeof_T);
	frame->state = 1;
	tick(1, frame->resumer, Holder_across_asyncComplete0, frame);
	return;
__async_state1: ;
	frame->x = frame->await0_result;
	__builtin_memmove(copy, frame->this->slot, frame->this->_sizeof_T);
	frame->complete(frame->complete_context, frame->x);
	free(frame);
	return;
}

static uintptr_t sizeVia(uintptr_t sizeof_T)
{
	sizeVia_lambdaContext0 _lambdaContext1;
	_lambdaContext1.capture0_sizeof_T = &sizeof_T;
	uintptr_t (* read)(void *arg0) = sizeVia_lambda0;
	void *read_context = &_lambdaContext1;
	return read(read_context);
}

static void copyVia(void *values, uintptr_t values_length, uintptr_t from, uintptr_t to, uintptr_t sizeof_T)
{
	copyVia_lambdaContext1 _lambdaContext2;
	_lambdaContext2.capture0_values = &values;
	_lambdaContext2.capture1_values_length = &values_length;
	_lambdaContext2.capture2_to = &to;
	_lambdaContext2.capture3_from = &from;
	_lambdaContext2.capture4_sizeof_T = &sizeof_T;
	void (* move)(void *arg0) = copyVia_lambda1;
	void *move_context = &_lambdaContext2;
	move(move_context);
}

static retainedIter retained(void *items, uintptr_t sizeof_T, const char *typenameof_T, const Reader *vtableof_T_Reader)
{
	return (retainedIter){ .__state = 0, .items = items, ._sizeof_T = sizeof_T, ._typenameof_T = typenameof_T, ._vtableof_T_Reader = vtableof_T_Reader };
}

static uintptr_t produce(const void *value, uintptr_t count, void *buffer, uintptr_t buffer_length, uintptr_t sizeof_T)
{
	if ((buffer_length >= count))
	{
		for (uintptr_t i = 0; (i < count); i++)
		{
			__builtin_memmove((void*)(((uint8_t*)buffer) + (i * sizeof_T)), value, sizeof_T);
__for_continue6: ;
		}
__for_break7: ;
	}
	return count;
}

static uintptr_t prepared(const void *value, uintptr_t sizeof_T)
{
	uintptr_t _pr3 = produce(value, 3, NULL, 0, sizeof_T);
	void *_pe4 = (void*)(__builtin_alloca((sizeof_T * _pr3)));
	produce(value, 3, _pe4, _pr3, sizeof_T);
	void *_pa5 = _pe4;
	uintptr_t _prepArray5_length = _pr3;
	void *made = _pa5;
	uintptr_t made_length = _prepArray5_length;
	return made_length;
}

static void *identity(void *pointer)
{
	return pointer;
}

static void Resumer_resumeAsync(Resumer *this, void (* continuation)(void *arg0), void *continuation_context)
{
	continuation(continuation_context);
}

static void tick(int value, Resumer *resumer, void (* complete)(void *context, int result), void *complete_context)
{
	complete(complete_context, value);
	return;
}

static void Holder_across(Holder *this, Resumer *resumer, void (* complete)(void *context, int result), void *complete_context)
{
	Holder_across_asyncFrame *frame = NULL;
	if (frame == NULL)
	{
		frame = (Holder_across_asyncFrame *)malloc(sizeof(Holder_across_asyncFrame));
	}
	*frame = (Holder_across_asyncFrame){0};
	frame->complete = complete;
	frame->complete_context = complete_context;
	frame->this = this;
	frame->resumer = resumer;
	Holder_across_asyncResume(frame);
	return;
}

static void Holder_op_initnew(Holder *this, void *slot, uintptr_t sizeof_T)
{
	this->_sizeof_T = sizeof_T;
	this->slot = slot;
}

static Holder *Holder_create(void *slot, uintptr_t sizeof_T)
{
	Holder *_created0 = (Holder *)(malloc(sizeof(Holder)));
	if ((_created0 != NULL))
	{
		*_created0 = (Holder){0};
		Holder_op_initnew(_created0, slot, sizeof_T);
	}
	return _created0;
}

int main(void)
{
	Wide wide = (Wide){ 1, 2, 3 };
	return ((((Wide *)(identity(&wide)))->a == 1) ? 0 : 1);
}

static bool retainedIter_next(retainedIter *this, int *current)
{
	if ((this->__state == -1))
	{
		return false;
	}
	if ((this->__state == 1))
	{
		goto __iter_resume1;
	}
	if ((this->__state == 2))
	{
		goto __iter_resume2;
	}
	this->name = this->_typenameof_T;
	*current = this->_vtableof_T_Reader->read(this->items);
	this->__state = 1;
	return true;
__iter_resume1: ;
	;
	*current = (int)(this->_sizeof_T);
	this->__state = 2;
	return true;
__iter_resume2: ;
	;
	this->__state = -1;
	return false;
}

static void retainedIter_destroy(retainedIter *this)
{
	if ((this->__state != -1))
	{
		this->__state = -1;
	}
}

static bool retainedIter_op_iter(void *ctx, int *current)
{
	retainedIter *state = (retainedIter *)(ctx);
	if ((current == NULL))
	{
		retainedIter_destroy(state);
		return false;
	}
	return retainedIter_next(state, current);
}

static uintptr_t sizeVia_lambda0(void *_context)
{
	sizeVia_lambdaContext0 *lambdaContext = (sizeVia_lambdaContext0 *)(_context);
	return (*lambdaContext->capture0_sizeof_T);
}

static void copyVia_lambda1(void *_context)
{
	copyVia_lambdaContext1 *lambdaContext = (copyVia_lambdaContext1 *)(_context);
	__builtin_memmove((void*)(((uint8_t*)(*lambdaContext->capture0_values)) + ((*lambdaContext->capture2_to) * (*lambdaContext->capture4_sizeof_T))), (void*)(((uint8_t*)(*lambdaContext->capture0_values)) + ((*lambdaContext->capture3_from) * (*lambdaContext->capture4_sizeof_T))), (*lambdaContext->capture4_sizeof_T));
}

// file: generic_execution_state_abi.h
#ifndef GENERIC_EXECUTION_STATE_ABI_H_
#define GENERIC_EXECUTION_STATE_ABI_H_

#include "generic_execution_state_abi_private.h"

int main(void);

#endif
// file: generic_execution_state_abi_private.h
#ifndef GENERIC_EXECUTION_STATE_ABI_PRIVATE_H_
#define GENERIC_EXECUTION_STATE_ABI_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Reader Reader;
typedef struct Resumer Resumer;
typedef struct Holder Holder;
typedef struct Wide Wide;
typedef struct retainedIter retainedIter;
typedef struct sizeVia_lambdaContext0 sizeVia_lambdaContext0;
typedef struct copyVia_lambdaContext1 copyVia_lambdaContext1;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Reader
{
	int (* read)(void* ctx);
};
struct Wide
{
	int64_t a;
	uint8_t b;
	int16_t c;
};
struct retainedIter
{
	int __state;
	void *items;
	uintptr_t _sizeof_T;
	const char *_typenameof_T;
	const Reader *_vtableof_T_Reader;
	const char *name;
};
struct sizeVia_lambdaContext0
{
	uintptr_t *capture0_sizeof_T;
};
struct copyVia_lambdaContext1
{
	void **capture0_values;
	uintptr_t *capture1_values_length;
	uintptr_t *capture2_to;
	uintptr_t *capture3_from;
	uintptr_t *capture4_sizeof_T;
};
struct Resumer
{
	char _camp_empty;
};
struct Holder
{
	void *slot;
	uintptr_t _sizeof_T;
};

/* Function declarations. */
int main(void);

/* Object declarations. */


#endif
